using System;
using System.Text;
using System.Drawing;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.IO;
using System.Diagnostics;
using Microsoft.Win32;
using System.Reflection;

// 程序集元数据（文件属性 / 杀软判定友好）
[assembly: AssemblyTitle("VU Widgets")]
[assembly: AssemblyDescription("VU Widgets - Desktop VU Meter")]
[assembly: AssemblyCompany("༺格桑仁青༻ & Old Newbie")]
[assembly: AssemblyProduct("VU Widgets")]
[assembly: AssemblyCopyright("© 2026 ༺格桑仁青༻ & Old Newbie")]
[assembly: AssemblyVersion("3.6.9.0")]
[assembly: AssemblyFileVersion("3.6.9.0")]

namespace VolumeMeter
{
    // 窗口配置
    public class WindowSettings
    {
        public string Id { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public float Scale { get; set; }
        public int Skin { get; set; }
        public int Mode { get; set; }
        public double Opacity { get; set; }
        public bool AlwaysOnTop { get; set; }
        public bool StealthMode { get; set; }

        // v3.6.7：多显示器拔插「接管中」状态（断开前所在屏与坐标，接回后归位）
        public bool Parked { get; set; }
        public string ParkDevice { get; set; }
        public string ParkMon { get; set; }
        public int ParkX { get; set; }
        public int ParkY { get; set; }
    }
    
    // 音频电平数据（全局共享）
    public static class AudioData
    {
        // 系统输出音量
        public static float LeftRMS = 0f;
        public static float RightRMS = 0f;
        public static float MasterRMS = 0f;
        public static float SystemVolume = 0f;

        // v3.6.8a：神隐唤醒专用电平。RMS 是「-40dB..0dB → 0..100」的指针刻度，
        // 低于 -40dB 直接截断成 0，等于给神隐埋了一道 -38.8dB 的唤醒门限
        // （轻声说话、键盘、轻音乐的音量正好落在门限下方，所以小音量唤不醒）。
        // 这里另算一份不截断的电平：-80dB..0dB → 0..100，只有真正的数字静音才是 0。
        public static float LeftWake = 0f;
        public static float RightWake = 0f;
        public static float MasterWake = 0f;
        
        // 左右声道独立音量（0-100）
        public static float LeftChannelVolume = 0f;
        public static float RightChannelVolume = 0f;
        
        // 麦克风输入音量
        public static float MicRMS = 0f;
        public static float MicWake = 0f;
        public static float MicVolume = 0f;

        // v3.6.9b：Mic 模式的「音量」改为表内软件增益（0..1，默认 1.0），跨重启记忆。
        // 起因：实测本机采集端点（Headset 2- SM908）的 IAudioEndpointVolume 读得到也写得进
        // （写 0.5 读回 0.500），可采集样本一点不受影响 —— 端点音量对采集流无效，
        // 于是「音量关到 0」指针照样随声摆动，麦克风音量完全不受控。
        // 现在指针电平与唤醒电平都乘这个增益：增益 0 = 指针 0、百分比 0、神隐可入睡。
        public static float MicGain = 1f;

        // v3.6.9c：真静音。本机实测（同一条已打开的采集流）：
        //   scalar 0.000 → -69.6 dBFS；写 1.0 → -69.5；写 0.0 → -69.5（电平旋钮是假的，写了不生效）
        //   但 Mute=true → -240.0 dBFS，采集样本精确为 0；Mute=false → 恢复 -69.5
        // 结论：这只设备只认「静音」不认「电平」。所以 0 档直接接端点真静音（别的 App 也收不到音），
        // 中间档仍由表内增益负责显示。退出 Mic 模式时把我们按下去的静音还原给系统。
        private static bool micMuteOwned = false;      // 这个静音是我们按的
        private static bool micMuteOriginal = false;   // 按之前它是什么状态

        // ===== v3.6.9：麦克风三态（Off / Waiting / Active）=====
        // 3.6.8 及以前，Mic 采集初始化失败只是一句 MessageBox（还带原始异常文案），
        // 之后既没有再试入口（重连分支要求 inputReinitPending、健康检查要求 InputInitialized，
        // 两个标志在失败分支里都不成立），也没有设备接入监听 —— 结果就是「弹一次窗 + 指针永久不动」。
        // 3.6.9 改成状态机：失败即进入 Waiting，由设备事件 + 1Hz 兜底轮询自动重开，全程静默。
        public enum MicStatus { Off, Waiting, Active }
        public static MicStatus MicState = MicStatus.Off;
        // 最近一次失败的类型，决定「确定」按钮跳「声音设置」还是「隐私-麦克风」
        public static bool MicFailIsNoDevice = false;
        public static bool MicFailIsPermission = false;
        // IMMNotificationClient 回调线程只置位，实际探测由 UI 线程定时器消费
        private static volatile bool micProbeRequested = false;
        private static DateTime lastInputDataTime = DateTime.Now;
        private static MicNotifySink micNotifySink;
        private static MMDeviceEnumerator micNotifyEnumerator;
        private static int micTickCounter = 0;
        
        public static bool OutputInitialized = false;
        public static bool InputInitialized = false;
        
        private static WasapiLoopbackCapture outputCapture;
        private static WasapiCapture inputCapture;
        private static MMDevice defaultOutputDevice;
        private static MMDevice defaultInputDevice;
        private static string currentOutputDeviceId;
        private static string currentInputDeviceId;
        private static List<VUMeterForm> windows = new List<VUMeterForm>();
        private static object lockObj = new object();
        private static Timer volumeTimer;
        private static int healthCheckCounter = 0;
        private static DateTime lastOutputDataTime = DateTime.Now;
        
        // v3.6.4：重连状态机——由 UI 线程的 volumeTimer 统一驱动延迟重试，
        // 不再在 NAudio 回调线程上创建 WinForms Timer（无消息泵，Tick 永不执行）
        private static bool outputReinitPending = false;
        private static DateTime outputReinitRequestTime;
        private static int outputRetryCount = 0;
        // v3.6.9：输入侧的 inputReinitPending / inputReinitRequestTime / inputRetryCount 已删除，
        // 麦克风改由 MicStatus 三态 + 设备通知 + 1Hz 兜底驱动（见 MicState 一带的实现）
        
        // 界面语言："zh" 或 "en"
        public static string CurrentLanguage = "zh";

        // v3.6.2：彻底退出标志——置位后窗口关闭时不再把配置写回注册表
        public static bool SuppressSaveOnClose = false;

        // v3.6.4：保留设置退出标志——托盘"暂时退出（保留全部设置）"使用，
        // 置位后窗口关闭既不删除也不写回配置（修复原版误删全部配置的 BUG）
        public static bool KeepSettingsOnClose = false;

        // 系统托盘图标
        public static NotifyIcon trayIcon;
        public static bool temporarilyVisible = false;
        private static Timer temporaryVisibleTimer;
        
        // 注册表路径
        private const string WindowsKey = @"Software\VolumeMeter\Windows";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AppName = "VolumeMeter";
        
        public static void RegisterWindow(VUMeterForm form)
        {
            lock (lockObj)
            {
                windows.Add(form);
            }
        }
        
        public static void EnsureOutputInitialized()
        {
            if (!OutputInitialized && !outputReinitPending)
            {
                InitializeOutput(false);
            }
        }
        
        // v3.6：返回当前进程内所有存活窗口的快照，供窗口吸附等逻辑使用
        public static List<VUMeterForm> GetAllWindows()
        {
            lock (lockObj)
            {
                return new List<VUMeterForm>(windows);
            }
        }

        // v3.6.6：显示桌面穿透看门狗——每 200ms 检查桌面层窗口是否被抬升的桌面盖住，
        // 是则按序重升。独立于音频初始化（volumeTimer 可能因音频故障不存在）。
        private static Timer desktopCheckTimer;
        public static void EnsureDesktopWatcher()
        {
            if (desktopCheckTimer != null) return;
            desktopCheckTimer = new Timer();
            desktopCheckTimer.Interval = 200;
            desktopCheckTimer.Tick += (s, e) =>
            {
                try { VUMeterForm.CheckDesktopWindows(); } catch { }
                // v3.6.7：看门狗心跳顺带刷新各表盘所在屏记忆 + 接屏归位兜底（防事件漏收/休眠错过）
                try { VUMeterForm.HeartbeatScreens(); } catch { }
            };
            desktopCheckTimer.Start();
        }

        public static void UnregisterWindow(VUMeterForm form)
        {
            // v3.6.4：修复死锁——原版在持有 lockObj 时调用 StopRecording()，
            // 该调用会同步等待采集线程退出，而采集线程可能正卡在
            // OnOutputDataAvailable 里等待同一把锁 → 互等挂起。
            // 现改为：锁内只做"摘引用 + 置空"，锁外再 Stop/Dispose。
            WasapiLoopbackCapture oldOutput = null;
            WasapiCapture oldInput = null;
            Timer oldVolumeTimer = null;
            NotifyIcon oldTray = null;
            bool lastWindow = false;
            
            lock (lockObj)
            {
                windows.Remove(form);
                
                if (windows.Count == 0)
                {
                    oldOutput = outputCapture; outputCapture = null;
                    oldInput = inputCapture; inputCapture = null;
                    oldVolumeTimer = volumeTimer; volumeTimer = null;
                    oldTray = trayIcon; trayIcon = null;
                    
                    OutputInitialized = false;
                    InputInitialized = false;
                    outputReinitPending = false;
                    MicState = MicStatus.Off;
                    micProbeRequested = false;
                    lastWindow = true;
                }
            }
            
            if (lastWindow)
            {
                // v3.6.9c：彻底退出前把 Mic 的真静音还回去（否则表一退出麦克风就一直是哑的）
                try { ReleaseMicEndpointMute(); } catch {}
                try { if (oldOutput != null) oldOutput.StopRecording(); } catch {}
                try { if (oldOutput != null) oldOutput.Dispose(); } catch {}
                try { if (oldInput != null) oldInput.StopRecording(); } catch {}
                try { if (oldInput != null) oldInput.Dispose(); } catch {}
                try { if (oldVolumeTimer != null) { oldVolumeTimer.Stop(); oldVolumeTimer.Dispose(); } } catch {}
                try { if (oldTray != null) { oldTray.Visible = false; oldTray.Dispose(); } } catch {}
                UnregisterMicNotifications();
                
                Application.Exit();
            }
        }
        
        // ========== v3.6.9 麦克风状态机 ==========

        // 采集端点的增删/启停/默认设备变化回调。
        // 回调运行在 audiosrv 的 COM 线程上，这里只置一个标志，
        // 真正的打开动作交给 UI 线程的 volumeTimer 消费（≤100ms 响应），避免跨线程动设备对象。
        private class MicNotifySink : NAudio.CoreAudioApi.Interfaces.IMMNotificationClient
        {
            public void OnDeviceStateChanged(string deviceId, DeviceState newState) { micProbeRequested = true; }
            public void OnDeviceAdded(string pwstrDeviceId) { micProbeRequested = true; }
            public void OnDeviceRemoved(string deviceId) { micProbeRequested = true; }
            public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
            {
                if (flow == DataFlow.Capture) micProbeRequested = true;
            }
            public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
        }

        private static void RegisterMicNotifications()
        {
            if (micNotifySink != null) return;
            try
            {
                micNotifyEnumerator = new MMDeviceEnumerator();
                micNotifySink = new MicNotifySink();
                micNotifyEnumerator.RegisterEndpointNotificationCallback(micNotifySink);
            }
            catch
            {
                // 注册失败只是失去「即时唤醒」，1Hz 兜底轮询仍在，不影响功能
                micNotifySink = null;
            }
        }

        private static void UnregisterMicNotifications()
        {
            try { if (micNotifyEnumerator != null && micNotifySink != null) micNotifyEnumerator.UnregisterEndpointNotificationCallback(micNotifySink); } catch { }
            try { if (micNotifyEnumerator != null) micNotifyEnumerator.Dispose(); } catch { }
            micNotifySink = null;
            micNotifyEnumerator = null;
        }

        // 失败归类：无端点 / 权限被拒 / 端点打不开（三种都要能区分，因为「确定」跳的设置页不同）
        private enum MicFailKind { None, NoDevice, Permission, Unavailable }

        // 真正去打开一路采集。失败时保证不留半初始化对象（3.6.8 的僵尸 capture 就是这里漏的）
        private static MicFailKind OpenMicCapture(out string detail)
        {
            detail = "";
            MMDeviceEnumerator en = null;
            WasapiCapture cap = null;
            try
            {
                en = new MMDeviceEnumerator();

                int active = 0;
                try { active = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Count; }
                catch { active = 0; }
                if (active <= 0) { detail = "no active capture endpoint"; return MicFailKind.NoDevice; }

                MMDevice dev = null;
                try { dev = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); }
                catch { dev = null; }
                if (dev == null)
                {
                    // 默认端点取不到（Role 配置异常/设备刚插入尚未设为默认）时，退用第一个 Active 端点
                    MMDeviceCollection col = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                    if (col.Count <= 0) { detail = "no device"; return MicFailKind.NoDevice; }
                    dev = col[0];
                }

                cap = new WasapiCapture(dev);
                cap.DataAvailable += OnInputDataAvailable;
                cap.RecordingStopped += OnInputStopped;
                cap.StartRecording();

                lock (lockObj)
                {
                    inputCapture = cap;
                    defaultInputDevice = dev;
                    currentInputDeviceId = dev.ID;
                }
                cap = null; // 所有权已移交 inputCapture 字段
                InputInitialized = true;
                MicState = MicStatus.Active;
                lastInputDataTime = DateTime.Now;
                micProbeRequested = false;
                // v3.6.9c：新开流后立刻按当前增益状态对齐端点的真静音（增益记忆为 0 时别让它漏音）
                ApplyMicEndpointMute();
                return MicFailKind.None;
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + " hr=0x" + (ex.HResult & 0xFFFFFFFF).ToString("X8") + " " + ex.Message;
                // E_ACCESSDENIED = 被隐私开关/其他程序拦住，去隐私页才有用
                MicFailKind kind = (ex.HResult == unchecked((int)0x80070005)) ? MicFailKind.Permission : MicFailKind.Unavailable;
                return kind;
            }
            finally
            {
                if (cap != null) { try { cap.StopRecording(); } catch { } try { cap.Dispose(); } catch { } }
                if (en != null) { try { en.Dispose(); } catch { } }
            }
        }

        // 丢弃采集并落到指定状态；电平一并清零，避免冻结值把神隐顶住
        private static void DropMicCapture(MicStatus next)
        {
            // v3.6.9c：先把我们按下的静音还回去（此时 defaultInputDevice 还没被清空）
            ReleaseMicEndpointMute();

            WasapiCapture old;
            lock (lockObj)
            {
                old = inputCapture;
                inputCapture = null;
                defaultInputDevice = null;
                currentInputDeviceId = null;
                MicRMS = 0f;
                MicWake = 0f;
                MicVolume = MicGain * 100f;   // v3.6.9b：增益是持久的，掉到等待态也不清成 0
            }
            InputInitialized = false;
            MicState = next;
            if (old != null)
            {
                try { old.StopRecording(); } catch { }
                try { old.Dispose(); } catch { }
            }
        }

        // 进入 Mic 模式（用户主动切换、新建窗口、启动读配置共用；本身从不弹窗）
        public static bool BeginMicMode()
        {
            if (MicState == MicStatus.Active && InputInitialized && inputCapture != null) return true;

            string detail;
            MicFailKind kind = OpenMicCapture(out detail);
            if (kind == MicFailKind.None)
            {
                MicFailIsNoDevice = false;
                MicFailIsPermission = false;
                RegisterMicNotifications();
                return true;
            }

            MicFailIsNoDevice = (kind == MicFailKind.NoDevice);
            MicFailIsPermission = (kind == MicFailKind.Permission);
            DropMicCapture(MicStatus.Waiting);
            RegisterMicNotifications();
            return false;
        }

        // 退出 Mic 设定（用户选「取消」或所有窗口都不再用 Mic）
        public static void EndMicMode()
        {
            DropMicCapture(MicStatus.Off);
        }

        // 等待态探测：由 volumeTimer 在 UI 线程调用
        public static void PumpMicWaiting()
        {
            if (MicState != MicStatus.Waiting) { micProbeRequested = false; return; }
            if (GetAllWindows().Count == 0) { DropMicCapture(MicStatus.Off); return; }

            string detail;
            if (OpenMicCapture(out detail) == MicFailKind.None) return;
            // 仍在等待：保持静默，绝不弹窗
        }

        // 统一收口：有窗口要 Mic 就保证在跑/在等，没人要就释放
        public static void RefreshMicDemand()
        {
            bool demand = false;
            lock (lockObj)
            {
                foreach (VUMeterForm f in windows)
                {
                    if (f != null && !f.IsDisposed && f.mode == MeterMode.Microphone) { demand = true; break; }
                }
            }
            if (!demand)
            {
                if (MicState != MicStatus.Off) EndMicMode();
                return;
            }
            if (MicState == MicStatus.Active && InputInitialized && inputCapture != null) return;
            if (MicState == MicStatus.Off) BeginMicMode();
            // Waiting 状态交给定时器继续探测，不在这里重复打开
        }
        
        private static void InitializeOutput(bool silent)
        {
            try
            {
                try {
                    if (volumeTimer != null)
                    {
                        volumeTimer.Stop();
                        volumeTimer.Dispose();
                        volumeTimer = null;
                    }
                } catch {}
                
                MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
                defaultOutputDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                currentOutputDeviceId = defaultOutputDevice.ID;
                
                outputCapture = new WasapiLoopbackCapture();
                outputCapture.DataAvailable += OnOutputDataAvailable;
                outputCapture.RecordingStopped += OnOutputStopped;
                outputCapture.StartRecording();
                
                volumeTimer = new Timer();
                volumeTimer.Interval = 100;
                volumeTimer.Tick += (s, e) => {
                    try {
                        SystemVolume = defaultOutputDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100f;
                        // 读取左右声道独立音量
                        var channels = defaultOutputDevice.AudioEndpointVolume.Channels;
                        if (channels.Count >= 2)
                        {
                            LeftChannelVolume = channels[0].VolumeLevelScalar * 100f;
                            RightChannelVolume = channels[1].VolumeLevelScalar * 100f;
                        }
                    } catch {}
                    // v3.6.9b：Mic 的百分比/进度条只认表内增益，不再读端点标量
                    // （端点标量在本机恒为 0 却不影响采集，读它只会让显示与实际电平打架）
                    MicVolume = MicGain * 100f;
                    
                    // 检测默认输出设备是否变化
                    try {
                        MMDeviceEnumerator enum2 = new MMDeviceEnumerator();
                        MMDevice newDevice = enum2.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        if (newDevice != null && newDevice.ID != currentOutputDeviceId)
                        {
                            currentOutputDeviceId = newDevice.ID;
                            ReinitializeOutput();
                        }
                    } catch {}
                    
                    // v3.6.9：默认输入设备变化 → 丢掉现有采集，退回等待态，由等待驱动立刻重开
                    try {
                        if (MicState == MicStatus.Active && defaultInputDevice != null)
                        {
                            MMDeviceEnumerator enum3 = new MMDeviceEnumerator();
                            MMDevice newInput = enum3.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                            if (newInput != null && newInput.ID != currentInputDeviceId)
                            {
                                DropMicCapture(MicStatus.Waiting);
                                micProbeRequested = true;
                            }
                        }
                    } catch {}
                    
                    // 静音衰减：WasapiLoopbackCapture在完全静音时可能停止回调，
                    // 导致RMS停留在最后一个非零值，神隐模式误以为一直有声音。
                    // 超过200ms没收到数据则主动衰减，确保静音时RMS能降到0。
                    if ((DateTime.Now - lastOutputDataTime).TotalMilliseconds > 200)
                    {
                        lock (lockObj)
                        {
                            LeftRMS *= 0.85f;
                            RightRMS *= 0.85f;
                            MasterRMS *= 0.85f;
                            // v3.6.8a：唤醒电平同步衰减——否则回调停止时电平冻结，神隐永不入睡
                            LeftWake *= 0.85f;
                            RightWake *= 0.85f;
                            MasterWake *= 0.85f;
                        }
                    }
                    
                    // v3.6.4：消费"输出重连"请求（统一在 UI 线程执行，500ms 延迟给设备切换留时间）
                    if (outputReinitPending && outputCapture == null &&
                        (DateTime.Now - outputReinitRequestTime).TotalMilliseconds >= 500)
                    {
                        outputReinitPending = false;
                        if (GetAllWindows().Count > 0)
                        {
                            InitializeOutput(true);
                        }
                    }
                    
                    // v3.6.9：Mic 电平同步衰减——等待态或采集中断超过 200ms 必须回零，
                    // 否则最后一段电平会冻结，神隐模式被这个假电平顶住永不入睡
                    if (MicState != MicStatus.Active ||
                        (DateTime.Now - lastInputDataTime).TotalMilliseconds > 200)
                    {
                        lock (lockObj)
                        {
                            MicRMS *= 0.85f;
                            MicWake *= 0.85f;
                        }
                    }

                    // 健康检查：每5秒检测一次捕获是否还在运行
                    healthCheckCounter++;
                    if (healthCheckCounter >= 50)
                    {
                        healthCheckCounter = 0;
                        try {
                            bool needReinit = false;
                            
                            // 检查1：捕获已停止或引用丢失
                            if (outputCapture != null && outputCapture.CaptureState == CaptureState.Stopped)
                            {
                                needReinit = true;
                            }
                            if (outputCapture == null && !OutputInitialized && !outputReinitPending)
                            {
                                needReinit = true;
                            }
                            
                            // 检查2：数据新鲜度——超过5秒没收到音频数据，说明捕获可能假死
                            if ((DateTime.Now - lastOutputDataTime).TotalSeconds > 5)
                            {
                                needReinit = true;
                            }
                            
                            if (needReinit)
                            {
                                ReinitializeOutput();
                            }
                        } catch {}
                    }
                    
                    // v3.6.9：等待接入驱动——设备事件到达时立刻探测（≤100ms 响应），
                    // 另加 1Hz 兜底（实测枚举+取默认端点共约 5.6ms，占比 0.5% 单核，仅等待态才有）
                    micTickCounter++;
                    if (MicState == MicStatus.Waiting)
                    {
                        if (micProbeRequested || micTickCounter >= 10)
                        {
                            micTickCounter = 0;
                            micProbeRequested = false;
                            PumpMicWaiting();
                        }
                    }
                    else
                    {
                        micTickCounter = 0;
                    }

                    // v3.6.9：Mic 采集健康检查——意外停止就退回等待态（不弹窗），随后自动重开
                    try {
                        if (MicState == MicStatus.Active && inputCapture != null &&
                            inputCapture.CaptureState == CaptureState.Stopped)
                        {
                            DropMicCapture(MicStatus.Waiting);
                            micProbeRequested = true;
                        }
                    } catch {}
                };
                volumeTimer.Start();
                
                OutputInitialized = true;
                outputReinitPending = false;
                outputRetryCount = 0;
            }
            catch (Exception ex)
            {
                if (silent)
                {
                    // 静默重试：最多 5 次，避免系统短暂无音频设备时反复弹窗
                    outputRetryCount++;
                    if (outputRetryCount < 5)
                    {
                        outputReinitPending = true;
                        outputReinitRequestTime = DateTime.Now;
                    }
                }
                else
                {
                    string msg = (CurrentLanguage == "zh") ? 
                        "音频输出捕获初始化失败: " : "Failed to initialize audio output capture: ";
                    MessageBox.Show(msg + ex.Message);
                }
            }
        }
        
        // v3.6.4：线程安全的输出重连——可在任意线程调用（含 NAudio 回调线程）。
        // 只置状态 + 摘引用，实际重建由 UI 线程的 volumeTimer 延迟执行
        private static void ReinitializeOutput()
        {
            lock (lockObj)
            {
                lastOutputDataTime = DateTime.Now; // 重置时间戳，避免重连过程中重复触发
            }
            outputReinitPending = true;
            outputReinitRequestTime = DateTime.Now;
            OutputInitialized = false;
            
            WasapiLoopbackCapture oldCapture;
            lock (lockObj)
            {
                oldCapture = outputCapture;
                outputCapture = null;
            }
            if (oldCapture != null)
            {
                try { oldCapture.StopRecording(); } catch {}
                try { oldCapture.Dispose(); } catch {}
            }
        }
        
        // v3.6.9：InitializeInput / ReinitializeInput 已由上面的三项式状态机接管
        // （OpenMicCapture / DropMicCapture / PumpMicWaiting）。
        // 旧实现的两处致命点就出在这里：失败分支只 MessageBox 不置 inputReinitPending，
        // 且把半初始化的 WasapiCapture 留在字段里，导致之后既不会自动重试、也无法被健康检查捡回来。

        // v3.6.8b：指针刻度的 dB 映射。
        // 3.6.7 及以前是「-40dB..0dB 线性映射 0..100，-40dB 以下直接截断成 0」，
        // 等于给指针留了一道 −40dB 硬门限：外放音量开到 20% 时听得清清楚楚，指针却一格不动。
        // 现在把底端延伸到 −70dB，并保持 −20dB..0dB 这一段的斜率与读数完全不变：
        //   −70..−20dB  → 0..50 刻度（斜率 1 %/dB，轻声音也能明显推动指针）
        //   −20..0dB    → 50..100 刻度（斜率 2.5 %/dB，与原版逐点一致）
        // 实测：−48.8dB（0.5% 幅度的正弦，20% 音量播歌基本上就在这个量级）→ 21 刻度；
        // 旧映射同一信号是 0 刻度（死针）。想再灵敏一点/钝一点，只改这里的 −70f 一处即可。
        private static float ToPointerLevel(float db)
        {
            float level;
            if (db >= -20f) level = 50f + (db + 20f) * 50f / 20f;
            else if (db >= -70f) level = (db + 70f) * 50f / 50f;
            else level = 0f;
            return Math.Max(0f, Math.Min(100f, level));
        }

        private static void OnOutputDataAvailable(object sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded == 0) return;
            
            // v3.6.4：使用 sender 引用而非静态字段，避免与重连/释放竞态
            WasapiLoopbackCapture capture = sender as WasapiLoopbackCapture;
            if (capture == null) return;
            
            const int bytesPerSample = 4;
            int channels = capture.WaveFormat.Channels;
            if (channels < 1) return;
            
            // v3.6.4：按帧遍历——原版按样本序号 i 与 i+1 取声道，单声道时错读相邻样本；
            // samplesPerChannel 用帧数，不再因整除丢失余量
            int frameCount = e.BytesRecorded / (bytesPerSample * channels);
            
            float leftSum = 0f, rightSum = 0f;
            
            for (int f = 0; f < frameCount; f++)
            {
                int baseIndex = f * channels;
                float left = Math.Abs(BitConverter.ToSingle(e.Buffer, baseIndex * bytesPerSample));
                float right = channels >= 2
                    ? Math.Abs(BitConverter.ToSingle(e.Buffer, (baseIndex + 1) * bytesPerSample))
                    : left;
                
                leftSum += left * left;
                rightSum += right * right;
            }
            
            int samplesPerChannel = frameCount;
            float leftRMS = (float)Math.Sqrt(leftSum / samplesPerChannel);
            float rightRMS = (float)Math.Sqrt(rightSum / samplesPerChannel);
            
            // v3.6：撤销绝对噪声门限，让指针对极微弱声音也有响应，视觉更活泼
            float leftDb = 20f * (float)Math.Log10(leftRMS + 0.0001f);
            float rightDb = 20f * (float)Math.Log10(rightRMS + 0.0001f);

            // 映射范围从-50dB进一步压缩为-40dB起算，把正常声音拉伸到更大刻度区间
            float leftLevel = ToPointerLevel(leftDb);
            float rightLevel = ToPointerLevel(rightDb);
            float masterLevel = (leftLevel + rightLevel) / 2f;

            // v3.6.8a：神隐唤醒电平（不截断，-80dB 起算，只有数字静音才是 0）
            float leftWake = Math.Max(0f, Math.Min(100f, (leftDb + 80f) * 100f / 80f));
            float rightWake = Math.Max(0f, Math.Min(100f, (rightDb + 80f) * 100f / 80f));

            lock (lockObj)
            {
                // 数据层平滑加快到 0.5/0.5，让峰值快速到达
                LeftRMS = LeftRMS * 0.5f + leftLevel * 0.5f;
                RightRMS = RightRMS * 0.5f + rightLevel * 0.5f;
                MasterRMS = MasterRMS * 0.5f + masterLevel * 0.5f;
                // 唤醒电平不做平滑：声音一到就该醒，声音一停就该睡
                LeftWake = leftWake;
                RightWake = rightWake;
                MasterWake = (leftWake + rightWake) / 2f;
                lastOutputDataTime = DateTime.Now;
            }
            
            // v3.6.4：移除 NotifyWindows 的 BeginInvoke——每个窗口本就有 16ms 渲染
            // Timer 每帧调用 UpdateMeter() 同步数据，音频回调再调度消息纯属重复，
            // 且原实现在窗口销毁竞态下未捕获异常会导致整个进程崩溃
        }
        
        private static void OnInputDataAvailable(object sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded == 0) return;
            
            WasapiCapture capture = sender as WasapiCapture;
            if (capture == null) return;
            
            const int bytesPerSample = 4;
            int channels = capture.WaveFormat.Channels;
            if (channels < 1) return;
            
            int frameCount = e.BytesRecorded / (bytesPerSample * channels);
            
            float sum = 0f;
            
            for (int f = 0; f < frameCount; f++)
            {
                int baseIndex = f * channels;
                float sample = Math.Abs(BitConverter.ToSingle(e.Buffer, baseIndex * bytesPerSample));
                sum += sample * sample;
            }
            
            int samplesPerChannel = frameCount;
            float rms = (float)Math.Sqrt(sum / samplesPerChannel);
            
            // v3.6：撤销麦克风噪声门限
            float db = 20f * (float)Math.Log10(rms + 0.0001f);
            float level = ToPointerLevel(db);
            float wake = Math.Max(0f, Math.Min(100f, (db + 80f) * 100f / 80f));

            // v3.6.9b：按表内增益缩放。增益 0 时 level/wake 都精准为 0 —— 指针归零、百分比归零，
            // 神隐也能正常入睡（端点音量对采集流无效，这一步是唯一能把「0」真正做实的办法）
            level *= MicGain;
            wake *= MicGain;

            lock (lockObj)
            {
                MicRMS = MicRMS * 0.5f + level * 0.5f;
                MicWake = wake;
            }
            lastInputDataTime = DateTime.Now;
        }
        
        private static void OnOutputStopped(object sender, StoppedEventArgs e)
        {
            OutputInitialized = false;
            // 意外停止时尝试重新初始化（ReinitializeOutput 线程安全）
            if (GetAllWindows().Count > 0 && !outputReinitPending)
            {
                ReinitializeOutput();
            }
        }
        
        private static void OnInputStopped(object sender, StoppedEventArgs e)
        {
            // v3.6.9：麦克风拔掉/被独占/驱动重置导致采集停止。
            // 注意本回调跑在采集线程上，这里绝不能再 Stop/Dispose 自己（NAudio 会等待采集线程退出，
            // 自我 Join 会直接挂死）。只把标志置起来，真正的「落到等待态 + 释放对象」由
            // volumeTimer 的健康检查在 UI 线程上做，随后等待驱动自动重开。
            InputInitialized = false;
            micProbeRequested = true;
        }
        
        public static void AdjustSystemVolume(float delta)
        {
            try
            {
                if (defaultOutputDevice != null)
                {
                    float newVolume = Math.Max(0, Math.Min(1, 
                        defaultOutputDevice.AudioEndpointVolume.MasterVolumeLevelScalar + delta));
                    defaultOutputDevice.AudioEndpointVolume.MasterVolumeLevelScalar = newVolume;
                }
            } catch {}
        }
        
        // 调整指定声道音量（channel: 0=左, 1=右）
        public static void AdjustChannelVolume(int channel, float delta)
        {
            try
            {
                if (defaultOutputDevice != null)
                {
                    var channels = defaultOutputDevice.AudioEndpointVolume.Channels;
                    if (channel >= 0 && channel < channels.Count)
                    {
                        float cur = channels[channel].VolumeLevelScalar;
                        float newVol = Math.Max(0, Math.Min(1, cur + delta));
                        channels[channel].VolumeLevelScalar = newVol;
                    }
                }
            } catch {}
        }
        
        // 调整麦克风音量（v3.6.9b：改成表内软件增益，不再依赖端点音量）
        public static void AdjustMicVolume(float delta)
        {
            try
            {
                MicGain = Math.Max(0f, Math.Min(1f, MicGain + delta));
                MicVolume = MicGain * 100f;   // 百分比/进度条跟随增益，而不是端点标量
                SaveMicGain();
                ApplyMicEndpointMute();       // 0 档 = 端点真静音；离开 0 档把静音还回去
            }
            catch { }
        }

        // 增益持久化（HKCU\Software\VolumeMeter\MicGain），与语言设置同一层
        public static void LoadMicGain()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\VolumeMeter", false))
                {
                    if (key != null)
                    {
                        object v = key.GetValue("MicGain");
                        if (v != null)
                        {
                            float g = Convert.ToSingle(v);
                            MicGain = Math.Max(0f, Math.Min(1f, g));
                        }
                    }
                }
            }
            catch { }
            MicVolume = MicGain * 100f;
        }

        private static void SaveMicGain()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\VolumeMeter"))
                {
                    if (key != null) key.SetValue("MicGain", MicGain.ToString("F3"));
                }
            }
            catch { }
        }

        // v3.6.9c：把 MicGain 的状态映射到端点「真静音」
        // gain <= 0.0005 → 端点 Mute=true（真实静音，所有程序的采集样本都归零）
        // gain > 0       → 还原进入前的静音状态；不写电平标量（本机写了不生效，写了只会骗 Windows 的显示）
        public static void ApplyMicEndpointMute()
        {
            try
            {
                MMDevice dev;
                lock (lockObj) { dev = defaultInputDevice; }
                if (dev == null) return;
                AudioEndpointVolume ep = dev.AudioEndpointVolume;
                if (ep == null) return;

                if (MicGain <= 0.0005f)
                {
                    if (!micMuteOwned)
                    {
                        micMuteOriginal = ep.Mute;
                        micMuteOwned = true;
                    }
                    if (!ep.Mute) ep.Mute = true;
                }
                else if (micMuteOwned)
                {
                    if (ep.Mute != micMuteOriginal) ep.Mute = micMuteOriginal;
                    micMuteOwned = false;
                }
            }
            catch { }
        }

        // 释放采集 / 退出 Mic 时把静音归还系统（不能让表退出后麦克风一直哑着）
        private static void ReleaseMicEndpointMute()
        {
            try
            {
                if (!micMuteOwned) return;
                MMDevice dev;
                lock (lockObj) { dev = defaultInputDevice; }
                if (dev != null)
                {
                    AudioEndpointVolume ep = dev.AudioEndpointVolume;
                    if (ep != null && ep.Mute != micMuteOriginal) ep.Mute = micMuteOriginal;
                }
                micMuteOwned = false;
            }
            catch { micMuteOwned = false; }
        }
        
        public static string GenerateWindowId()
        {
            return Guid.NewGuid().ToString("N");
        }
        
        public static void SaveWindowSettings(WindowSettings ws)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(WindowsKey + "\\" + ws.Id))
                {
                    if (key != null)
                    {
                        key.SetValue("X", ws.X);
                        key.SetValue("Y", ws.Y);
                        key.SetValue("Scale", ws.Scale);
                        key.SetValue("Skin", ws.Skin);
                        key.SetValue("Mode", ws.Mode);
                        key.SetValue("Opacity", ws.Opacity);
                        key.SetValue("AlwaysOnTop", ws.AlwaysOnTop ? 1 : 0);
                        key.SetValue("StealthMode", ws.StealthMode ? 1 : 0);

                        // v3.6.7：多显示器拔插状态持久化（崩溃/拔屏后直接退出也不丢）
                        key.SetValue("Parked", ws.Parked ? 1 : 0);
                        key.SetValue("ParkDevice", ws.ParkDevice ?? "");
                        key.SetValue("ParkMon", ws.ParkMon ?? "");
                        key.SetValue("ParkX", ws.ParkX);
                        key.SetValue("ParkY", ws.ParkY);
                    }
                }
            }
            catch {}
        }
        
        public static void DeleteWindowSettings(string id)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(WindowsKey + "\\" + id, false);
            }
            catch {}
            
            // 若所有窗口配置都已删除，清除整个VolumeMeter注册表项（包括语言设置）
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(WindowsKey, false))
                {
                    if (key != null && key.SubKeyCount == 0)
                    {
                        Registry.CurrentUser.DeleteSubKeyTree(@"Software\VolumeMeter", false);
                    }
                }
            }
            catch {}
        }
        
        public static List<WindowSettings> LoadAllWindowSettings()
        {
            List<WindowSettings> result = new List<WindowSettings>();
            
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(WindowsKey, false))
                {
                    if (key != null)
                    {
                        string[] subKeyNames = key.GetSubKeyNames();
                        foreach (string id in subKeyNames)
                        {
                            try
                            {
                                using (RegistryKey winKey = key.OpenSubKey(id))
                                {
                                    if (winKey != null)
                                    {
                                        WindowSettings ws = new WindowSettings();
                                        ws.Id = id;
                                        ws.X = Convert.ToInt32(winKey.GetValue("X", 100));
                                        ws.Y = Convert.ToInt32(winKey.GetValue("Y", 100));
                                        ws.Scale = Convert.ToSingle(winKey.GetValue("Scale", 1.0));
                                        ws.Skin = Convert.ToInt32(winKey.GetValue("Skin", 0));
                                        ws.Mode = Convert.ToInt32(winKey.GetValue("Mode", 2));
                                        ws.Opacity = Convert.ToDouble(winKey.GetValue("Opacity", 1.0));
                                        ws.AlwaysOnTop = Convert.ToInt32(winKey.GetValue("AlwaysOnTop", 0)) == 1;
                                        ws.StealthMode = Convert.ToInt32(winKey.GetValue("StealthMode", 0)) == 1;

                                        // v3.6.7：读回多显示器拔插状态（旧版配置无这些键时取默认值）
                                        ws.Parked = Convert.ToInt32(winKey.GetValue("Parked", 0)) == 1;
                                        ws.ParkDevice = (winKey.GetValue("ParkDevice") ?? "").ToString();
                                        ws.ParkMon = (winKey.GetValue("ParkMon") ?? "").ToString();
                                        ws.ParkX = Convert.ToInt32(winKey.GetValue("ParkX", int.MinValue));
                                        ws.ParkY = Convert.ToInt32(winKey.GetValue("ParkY", int.MinValue));
                                        result.Add(ws);
                                    }
                                }
                            }
                            catch {}
                        }
                    }
                }
            }
            catch {}
            
            return result;
        }
        
        public static void ClearAllSettings()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(WindowsKey, false);
            }
            catch {}
            
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key != null)
                    {
                        key.DeleteValue(AppName, false);
                    }
                }
            }
            catch {}
        }
        
        public static bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key != null)
                    {
                        object value = key.GetValue(AppName);
                        return value != null;
                    }
                }
            }
            catch {}
            return false;
        }
        
        public static void SetAutoStart(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key != null)
                    {
                        if (enable)
                        {
                            string exePath = Application.ExecutablePath;
                            key.SetValue(AppName, "\"" + exePath + "\"");
                        }
                        else
                        {
                            key.DeleteValue(AppName, false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                string msg = (CurrentLanguage == "zh") ? 
                    "设置开机自启动失败: " : "Failed to set auto-start: ";
                MessageBox.Show(msg + ex.Message);
            }
        }

        // v3.6.7：程序默认开机自启动——仅首次运行写入一次（AutoStartDefaulted 标记）；
        // 此后完全以托盘菜单开关为准，用户手动关闭后不会被启动流程再次写回
        //（避免重蹈 v3.6.4 之前"每次启动强制写回"的问题）。
        // "消除一切设置彻底退出"会连标记一起清掉，属于预期行为（恢复出厂）。
        public static void EnsureDefaultAutoStart()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\VolumeMeter"))
                {
                    if (key == null) return;
                    if (key.GetValue("AutoStartDefaulted") != null) return;
                    key.SetValue("AutoStartDefaulted", 1);
                }
                if (!IsAutoStartEnabled()) SetAutoStart(true);
            }
            catch { }
        }
        
        // 自动检测系统语言（首次运行或未保存语言时调用）
        public static void AutoDetectLanguage()
        {
            // 先从注册表读取用户保存的语言
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\VolumeMeter", false))
                {
                    if (key != null)
                    {
                        object lang = key.GetValue("Language");
                        if (lang != null)
                        {
                            CurrentLanguage = lang.ToString();
                            return;
                        }
                    }
                }
            }
            catch { }
            
            // 没有保存的语言，自动检测系统语言
            try
            {
                string sysLang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                CurrentLanguage = (sysLang == "zh") ? "zh" : "en";
            }
            catch
            {
                CurrentLanguage = "zh"; // 默认中文
            }
            
            // 保存自动检测的结果
            SaveLanguage();
        }
        
        // 手动切换语言
        public static void SetLanguage(string lang)
        {
            CurrentLanguage = lang;
            SaveLanguage();
        }
        
        // 保存语言设置到注册表
        private static void SaveLanguage()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\VolumeMeter"))
                {
                    if (key != null)
                    {
                        key.SetValue("Language", CurrentLanguage);
                    }
                }
            }
            catch { }
        }
        
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);
        
        // 初始化系统托盘图标
        public static void InitTrayIcon()
        {
            if (trayIcon != null) return;
            
            trayIcon = new NotifyIcon();
            trayIcon.Visible = true;
            
            // 从嵌入资源加载图标
            try
            {
                using (System.IO.Stream s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("VolumeMeter.ico.png"))
                {
                    if (s != null)
                    {
                        using (Bitmap bmp = new Bitmap(s))
                        {
                            // v3.6.4：Icon.FromHandle 不拥有句柄，Clone 一份自持有副本并 DestroyIcon，
                            // 修复 HICON 句柄泄漏
                            IntPtr hIcon = bmp.GetHicon();
                            try
                            {
                                using (Icon fromHandle = Icon.FromHandle(hIcon))
                                {
                                    trayIcon.Icon = (Icon)fromHandle.Clone();
                                }
                            }
                            finally
                            {
                                DestroyIcon(hIcon);
                            }
                        }
                    }
                }
            }
            catch { }
            
            if (trayIcon.Icon == null)
            {
                trayIcon.Icon = SystemIcons.Application;
            }
            
            // 双击：显示全部窗口6秒
            trayIcon.DoubleClick += (s, e) => ShowAllTemporarily();
            
            // 右键菜单
            ContextMenu menu = new ContextMenu();
            
            MenuItem showItem = new MenuItem(CurrentLanguage == "zh" ? "显示全部窗口(6秒)" : "Show All (6s)");
            showItem.Click += (s, e) => ShowAllTemporarily();
            menu.MenuItems.Add(showItem);
            
            MenuItem disableStealthItem = new MenuItem(CurrentLanguage == "zh" ? "取消全部神隐模式" : "Disable All Stealth Mode");
            disableStealthItem.Click += (s, e) => DisableAllStealth();
            menu.MenuItems.Add(disableStealthItem);
            
            // v3.6.4：开机自启动改为用户可选项——原版每次启动强制 SetAutoStart(true)，
            // 用户手动清除自启动项后下次运行又被写回
            MenuItem autoStartItem = new MenuItem(CurrentLanguage == "zh" ? "开机自启动" : "Run at Startup");
            autoStartItem.Checked = IsAutoStartEnabled();
            autoStartItem.Click += (s, e) => {
                bool enable = !autoStartItem.Checked;
                SetAutoStart(enable);
                autoStartItem.Checked = IsAutoStartEnabled();
            };
            menu.MenuItems.Add(autoStartItem);

            // v3.6.7：托盘右键加入"关于"（内容复用表盘右键的"注意事项"对话框）
            MenuItem trayAboutItem = new MenuItem(CurrentLanguage == "zh" ? "关于" : "About");
            trayAboutItem.Click += (s, e) => { VUMeterForm.ShowAbout(); };
            menu.MenuItems.Add(trayAboutItem);
            
            menu.MenuItems.Add("-");
            
            MenuItem tempExitItem = new MenuItem(CurrentLanguage == "zh" ? "暂时退出（保留全部设置）" : "Exit (Keep Settings)");
            tempExitItem.Click += (s, e) => ExitAll();
            menu.MenuItems.Add(tempExitItem);

            MenuItem clearExitItem = new MenuItem(CurrentLanguage == "zh" ? "消除一切设置彻底退出" : "Clear All Settings & Exit");
            clearExitItem.Click += (s, e) => ClearAndExit();
            menu.MenuItems.Add(clearExitItem);
            
            trayIcon.ContextMenu = menu;
            trayIcon.Text = "VU Widgets v3.6.9";
        }
        
        // 临时显示所有窗口6秒
        public static void ShowAllTemporarily()
        {
            temporarilyVisible = true;
            
            if (temporaryVisibleTimer == null)
            {
                temporaryVisibleTimer = new Timer();
                temporaryVisibleTimer.Interval = 6000;
                temporaryVisibleTimer.Tick += (s, e) =>
                {
                    temporarilyVisible = false;
                    temporaryVisibleTimer.Stop();
                };
            }
            else
            {
                temporaryVisibleTimer.Stop();
            }
            temporaryVisibleTimer.Start();
        }
        
        // 取消所有窗口的神隐模式
        public static void DisableAllStealth()
        {
            lock (lockObj)
            {
                foreach (VUMeterForm form in windows)
                {
                    if (form.stealthMode)
                    {
                        form.stealthMode = false;
                        form.Opacity = form.userOpacity;
                        form.silenceFrames = 0;
                        form.stealthFading = false;
                        form.UpdateStealthCheck();
                        form.RequestSave();
                    }
                }
            }
        }
        
        // 退出所有窗口
        public static void ExitAll()
        {
            // v3.6.4：修复"保留全部设置"反而删除全部配置的 BUG。
            // 原版 form.Close() 的 CloseReason 就是 UserClosing，
            // 会走进 OnFormClosing 的 DeleteWindowSettings 分支。
            // 置位 KeepSettingsOnClose 后窗口关闭既不删除也不写回。
            KeepSettingsOnClose = true;
            
            try
            {
                if (trayIcon != null)
                {
                    trayIcon.Visible = false;
                    trayIcon.Dispose();
                    trayIcon = null;
                }
            }
            catch { }

            // v3.6.5：释放临时显形定时器，避免退出后 Timer 句柄残留
            try
            {
                if (temporaryVisibleTimer != null)
                {
                    temporaryVisibleTimer.Stop();
                    temporaryVisibleTimer.Dispose();
                    temporaryVisibleTimer = null;
                }
            }
            catch { }

            // v3.6.7：看门狗 Timer 退出时一并释放（与其他 Timer 退出即释放的规范一致，T2）
            try
            {
                if (desktopCheckTimer != null)
                {
                    desktopCheckTimer.Stop();
                    desktopCheckTimer.Dispose();
                    desktopCheckTimer = null;
                }
            }
            catch { }
            
            List<VUMeterForm> toClose = GetAllWindows();
            foreach (VUMeterForm form in toClose)
            {
                try { form.Close(); } catch { }
            }
            
            try { Application.Exit(); } catch { }

            // v3.6.5：退出流程结束后复位保留设置标志，避免同进程内残留影响后续关闭判定
            KeepSettingsOnClose = false;
        }

        // 消除一切设置后彻底退出：清理全部窗口配置、语言与自启动项，再退出
        public static void ClearAndExit()
        {
            SuppressSaveOnClose = true;
            try { ClearAllSettings(); } catch { }
            ExitAll();
        }
    }
    
    public enum MeterMode
    {
        Left,
        Right,
        Master,
        Microphone
    }
    
    public class VUMeterForm : Form
    {
        // v3.6.4：素材改为全窗口静态共享——原版每个窗口各自加载 18 张位图，
        // 多窗口时内存成倍浪费。素材只读，生命周期与进程一致
        private static Bitmap[] backgroundImages;
        private static Bitmap[] needleImages;
        private static Bitmap[] upImages;
        private static Bitmap[] modeImages; // 0=Ⓛ 1=Ⓡ 2=Ⓜ 3=Mic
        // v3.6.9：Mic 等待态把标识画成 30% 不透明用的 ImageAttributes（静态共享，只建一次）
        private static System.Drawing.Imaging.ImageAttributes modeDimAttrs;
        private static readonly object imageLoadLock = new object();
        private int currentSkin = 0; // 默认主题1 玄铁圆初
        
        private float currentValue = 0f;
        private float currentWake = 0f;   // v3.6.8a：神隐唤醒用的未截断电平（-80dB 起算）
        private float displayValue = 0f;
        private float displaySystemVolume = 0f;
        
        // v3.6.9：改为 internal，AudioData.RefreshMicDemand 需要读各窗口当前模式，
        // 判断「还有没有窗口在用 Mic」（没人用就释放采集）
        internal MeterMode mode = MeterMode.Master;
        private float scale = 1f;
        private bool alwaysOnTop = false;
        
        // 神隐模式
        public bool stealthMode = false;
        public double userOpacity = 1.0;
        public int silenceFrames = 0;
        // v3.6.1：标记是否已进入淡出阶段。淡出中不再被"补满"逻辑拉回，
        // 避免淡出中途透明度跌破 userOpacity 后被误判为未浮满而反复补显
        public bool stealthFading = false;
        private const int StealthDelayFrames = 375; // 6秒 / 16ms ≈ 375帧
        // v3.6.8a：删除低音量门限（原 StealthThreshold = 3f，等价于 -38.8dB 以下唤不醒，
        // 轻声、键盘、轻音乐全被这一道门槛吃掉）。唤醒判据改为"未截断电平 > 0"，
        // 即只有真正的数字静音（RMS 恰为 0）才算安静。
        private const float StealthThreshold = 0f;

        // v3.6.6：首帧透明淡入（防御性保留）。v3.6.5 中用于掩盖挂 owner 触发的层级重排帧；
        // v3.6.6 已无任何 owner 操作，仅作为显示瞬间的视觉缓冲。
        private bool introFading = false;
        private double introTargetOpacity = 1.0;
        private int introDelayFrames = 0; // 透明保持帧数：Show 后等桌面层落位稳定再恢复透明度
        
        private string windowId;
        
        // ================= v3.6.7：多显示器拔插（迁屏 + 重连归位） =================
        // 平时记住「我当前在哪块屏」；被拔时按设备名 + 显示器 DeviceID 记档并迁到主屏居中；
        // 接回后按档归位。详见《多显示器拔插与重连还原_解决方案.md》。
        private string lastDev = "";          // 最近一次确认所在屏的设备名（\\.\DISPLAYn）
        private string lastMon = "";          // 最近一次确认所在屏的显示器 DeviceID
        private bool parked = false;          // 是否处于「接管中」状态
        private string parkDevice = "";       // 断开前所在屏的设备名
        private string parkMon = "";          // 断开前所在屏的显示器 DeviceID
        private int parkX = int.MinValue;     // 断开前坐标（int.MinValue = 未记档）
        private int parkY = int.MinValue;
        
        private bool dragging = false;
        // v3.6 窗口吸附：自实现拖拽以便在移动过程中检测与其他窗口的距离
        private Point dragOffset;           // 光标相对窗口左上角的偏移
        private const int SnapDistance = 16; // 吸附阈值（像素）
        
        private float needleCenterX = 117f;
        private float needleCenterY = 126f;
        // v3.6.1：起点再顺时针偏移3°，止点80°，摆幅更灵活
        private float startAngle = -56f;
        private float endAngle = 80f;
        
        private ContextMenuStrip contextMenu;
        private ToolStripMenuItem modeMenu;
        private ToolStripMenuItem themeMenu;
        private ToolStripMenuItem opacityMenu;
        private ToolStripMenuItem sizeMenu;
        private ToolStripMenuItem topMostMenu;
        private ToolStripMenuItem stealthMenuItem;
        
        // v3.6.4：渲染 Timer 与保存 Timer 提升为字段——原版渲染 Timer 是构造函数局部变量，
        // 窗口关闭后继续以 60fps 空转（闭包钉住已释放窗体）；saveTimer 未停止时还会把
        // 刚删除的注册表配置写回（"设置复活"）
        private Timer renderTimer;
        private Timer saveTimer;
        
        // v3.6.4：OnPaint 缓存——字体随 scale 变化重建，画刷全局共享，
        // 原版每帧（60fps）都 new Font/SolidBrush 产生大量 GDI 分配
        private static readonly SolidBrush grayBrush = new SolidBrush(Color.FromArgb(116, 116, 116));
        private static readonly SolidBrush volBrush = new SolidBrush(Color.FromArgb(153, 153, 153));
        private static readonly SolidBrush redBrush = new SolidBrush(Color.FromArgb(153, 255, 0, 0));
        private Font volFontCache;
        private float volFontCacheScale = -1f;
        private Font modeFontCache;
        private float modeFontCacheScale = -1f;
        
        // ================= v3.6.6：Win+D 穿透（无 owner + 显示桌面检测重升） =================
        // 依据（2026-11 探针实测 + Raymond Chen 2024-10 文章）：
        //  1. 表盘窗口无 WS_MINIMIZEBOX，Win+D 不会最小化它们（探针W实测零消息）；
        //  2. 但"显示桌面"会把桌面窗口（Progman/WorkerW）抬升到普通窗口之上，把表盘盖住；
        //  3. 检测到"桌面在表盘之上"时，把桌面层窗口按固定顺序重升到桌面窗口正上方即可
        //     恢复可见（探针B实测有效，无 band 副作用，还原后自动回到桌面层位置）。
        //  4. 不再使用 SHELLDLL_DefView owner（灰色用法，花屏根因），
        //     也不挂顶层桌面窗口（探针B实测无法随抬升的桌面走）。

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetTopWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint nCmd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_NOACTIVATE = 0x08000000; // v3.6.7：不抢前台焦点（L2）
        private const int WS_MINIMIZEBOX = 0x00020000;   // v3.6.7：Win+D 最小化位（T1 显式剥离）
        private const uint GW_HWNDNEXT = 2;
        private const uint GW_HWNDPREV = 3;
        // NOSIZE|NOMOVE|NOACTIVATE|NOOWNERZORDER|NOSENDCHANGING：纯 Z 序调整（同 Rainmeter ZPOS_FLAGS）
        private const uint SWP_ZFLAGS = 0x1 | 0x2 | 0x10 | 0x200 | 0x400;

        // v3.6.7：显示器 DeviceID 查询（设备名 \\.\DISPLAYn 会被系统重排编号，
        // 物理屏的 DeviceID 不变，作为归位时的副判据）
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);
        
        public VUMeterForm()
        {
            windowId = AudioData.GenerateWindowId();
            
            EnsureImagesLoaded();

            // v3.6.6：不再查询 DefView / 不再挂 owner（见下方 Win+D 穿透区块与 PrepareBeforeShow 注释）

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = false;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            Opacity = 1.0;
            userOpacity = 1.0;
            
            // v3.6.1：窗口尺寸和轴心基于当前主题（默认复古原初）的图片尺寸
            ClientSize = new Size(
                (int)(backgroundImages[currentSkin].Width * scale),
                (int)(backgroundImages[currentSkin].Height * scale)
            );
            
            needleCenterX = backgroundImages[currentSkin].Width / 2f;
            needleCenterY = backgroundImages[currentSkin].Height * 0.6f + 16f;
            
            CreateContextMenu();
            ContextMenuStrip = contextMenu;
            
            AudioData.RegisterWindow(this);
            
            this.LocationChanged += (s, e) => { RequestSave(); };
            
            saveTimer = new Timer();
            saveTimer.Interval = 300;
            saveTimer.Tick += (s, e) => {
                saveTimer.Stop();
                SaveCurrentWindow();
            };
            
            // v3.6：移除"上2/3穿透、下1/3可点"分区穿透，全窗口可点击可拖动

            RequestSave();
            
            renderTimer = new Timer();
            renderTimer.Interval = 16;
            renderTimer.Tick += (s, e) => {
                // 每帧更新currentValue，不依赖音频回调的BeginInvoke。
                // 音频回调停止时（如播放器退出），currentValue会冻结在最后值，
                // 导致神隐模式误以为一直有声音。此处主动同步，配合静音衰减确保RMS能降到0。
                UpdateMeter();
                
                // 非对称平滑：v3.6 进一步加快 attack，让指针峰值更跳；release 保持缓慢回落
                if (currentValue > displayValue)
                    displayValue = displayValue * 0.1f + currentValue * 0.9f;
                else
                    displayValue = displayValue * 0.85f + currentValue * 0.15f;
                
                // 根据显示模式选择对应的音量值用于进度条和数字显示
                float targetVolume = AudioData.SystemVolume;
                if (mode == MeterMode.Left) targetVolume = AudioData.LeftChannelVolume;
                else if (mode == MeterMode.Right) targetVolume = AudioData.RightChannelVolume;
                else if (mode == MeterMode.Microphone) targetVolume = AudioData.MicVolume;
                displaySystemVolume = displaySystemVolume * 0.9f + targetVolume * 0.1f;
                
                // 首帧淡入优先于神隐状态机：窗口可见后把透明度从 0 快速抬到目标值
                if (introFading && this.Visible)
                {
                    // 先保持透明若干帧，等桌面层落位在 DWM 侧稳定；之后直接恢复目标透明度，
                    // 不走半透明渐变，避免错位帧以半透明状态被看到
                    if (introDelayFrames > 0)
                    {
                        introDelayFrames--;
                    }
                    else
                    {
                        Opacity = introTargetOpacity;
                        introFading = false;
                    }
                }
                // 神隐模式逻辑
                else if (stealthMode)
                {
                    // 托盘图标双击临时显形：强制显示到用户设置透明度
                    if (AudioData.temporarilyVisible)
                    {
                        if (Opacity < userOpacity)
                        {
                            Opacity = Math.Min(userOpacity, Opacity + 0.15);
                        }
                        silenceFrames = 0;
                        stealthFading = false;
                    }
                    else if (currentWake > StealthThreshold)
                    {
                        silenceFrames = 0;
                        stealthFading = false; // 有声音，退出淡出状态
                        // v3.6：淡入速度从 0.05 提到 0.2，声音出现后约 5 帧（80ms）内满幅浮现
                        if (Opacity < userOpacity)
                        {
                            Opacity = Math.Min(userOpacity, Opacity + 0.2);
                        }
                    }
                    else
                    {
                        // 静音分支
                        // v3.6.1：仅在"尚未进入淡出、且窗口确实可见但未浮满"时补满，
                        // 解决短促提示音下滞后窗口卡在半透明；一旦进入淡出阶段只淡出、不再补显
                        if (!stealthFading && Opacity > 0 && Opacity < userOpacity)
                        {
                            Opacity = Math.Min(userOpacity, Opacity + 0.2);
                            silenceFrames = 0; // 补满期间重置静默计时，6秒延迟从浮满后开始
                        }
                        else
                        {
                            stealthFading = true; // 标记进入淡出阶段
                            silenceFrames++;
                            if (silenceFrames >= StealthDelayFrames)
                            {
                                // 淡出到 0（保持缓慢，避免突兀）
                                if (Opacity > 0)
                                {
                                    Opacity = Math.Max(0, Opacity - 0.05);
                                }
                                else
                                {
                                    stealthFading = false; // 完全隐藏后复位，等待下次声音浮现
                                }
                            }
                        }
                    }
                }
                
                // v3.6.4：完全隐藏（透明度 0）时跳过重绘，节省 CPU
                if (Opacity > 0)
                {
                    Invalidate();
                }
            };
            renderTimer.Start();
        }
        
        // v3.6.4：加载全部主题素材（全窗口共享，仅一次）
        private static void EnsureImagesLoaded()
        {
            if (backgroundImages != null) return;
            lock (imageLoadLock)
            {
                if (backgroundImages != null) return;
                
                Assembly asm = Assembly.GetExecutingAssembly();
                backgroundImages = new Bitmap[6];
                needleImages = new Bitmap[6];
                upImages = new Bitmap[6];
                
                for (int i = 0; i < 6; i++)
                {
                    try
                    {
                        // v3.6.2：六个主题素材与索引一一对应（i → layer/n/up (i+1)）
                        string bgResource = "VolumeMeter.layer" + (i+1) + ".png";
                        string needleResource = "VolumeMeter.n" + (i+1) + ".png";
                        string upResource = "VolumeMeter.up" + (i+1) + ".png";
                        
                        using (Stream bgStream = asm.GetManifestResourceStream(bgResource))
                        {
                            backgroundImages[i] = bgStream != null ? new Bitmap(bgStream) : CreateDefaultBackground();
                        }
                        using (Stream needleStream = asm.GetManifestResourceStream(needleResource))
                        {
                            needleImages[i] = needleStream != null ? new Bitmap(needleStream) : CreateDefaultNeedle();
                        }
                        using (Stream upStream = asm.GetManifestResourceStream(upResource))
                        {
                            upImages[i] = upStream != null ? new Bitmap(upStream) : CreateDefaultUp();
                        }
                    }
                    catch
                    {
                        backgroundImages[i] = CreateDefaultBackground();
                        needleImages[i] = CreateDefaultNeedle();
                        upImages[i] = CreateDefaultUp();
                    }
                }
                
                // 加载模式标识图片
                // v3.6.4：资源名改为 ASCII（原版"Ⓛ.png"等非 ASCII 资源名在 resgen
                // 路径下会被转义成乱码名而静默加载失败）
                string[] modeResources = { "VolumeMeter.L.png", "VolumeMeter.R.png", "VolumeMeter.M.png", "VolumeMeter.Mic.png" };
                modeImages = new Bitmap[4];
                for (int i = 0; i < 4; i++)
                {
                    try
                    {
                        using (Stream s = asm.GetManifestResourceStream(modeResources[i]))
                        {
                            modeImages[i] = s != null ? new Bitmap(s) : null;
                        }
                    }
                    catch { modeImages[i] = null; }
                }
            }
        }
        
        private static Bitmap CreateDefaultBackground()
        {
            Bitmap bmp = new Bitmap(234, 183);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.DarkGray);
                g.DrawEllipse(Pens.Black, 20, 20, 194, 143);
            }
            return bmp;
        }
        
        private static Bitmap CreateDefaultNeedle()
        {
            Bitmap bmp = new Bitmap(6, 86);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.FillRectangle(Brushes.Red, 2, 0, 2, 86);
            }
            return bmp;
        }
        
        private static Bitmap CreateDefaultUp()
        {
            Bitmap bmp = new Bitmap(70, 87);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.FillEllipse(Brushes.Black, 0, 40, 70, 47);
            }
            return bmp;
        }
        
        public void ApplySettings(WindowSettings settings)
        {
            windowId = settings.Id;
            
            // v3.6.4：注册表脏数据防护——Skin/Mode/Scale/Opacity 全部夹取到合法范围，
            // 原版直接索引 backgroundImages[settings.Skin]，脏数据（如 Skin=9）启动即崩
            scale = Math.Max(0.6f, Math.Min(1.8f, settings.Scale));
            currentSkin = Math.Max(0, Math.Min(backgroundImages.Length - 1, settings.Skin));
            int modeVal = Math.Max(0, Math.Min(3, settings.Mode));
            mode = (MeterMode)modeVal;
            userOpacity = Math.Max(0.2, Math.Min(1.0, settings.Opacity));
            
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(
                (int)(backgroundImages[currentSkin].Width * scale),
                (int)(backgroundImages[currentSkin].Height * scale)
            );
            needleCenterX = backgroundImages[currentSkin].Width / 2f;
            needleCenterY = backgroundImages[currentSkin].Height * 0.6f + 16f;
            
            // v3.6.4：位置校验——分辨率改变/拔掉显示器后原坐标可能完全出屏，
            // 配合神隐模式窗口将无法找回；出屏时回退到主屏工作区居中
            // v3.6.7：Park 状态随配置读回（原坐标存在 ParkX/ParkY，不会被 EnsureOnScreen 改写；
            // 归位动作延后到 OnLoad，届时句柄已建、屏幕拓扑也已就绪）
            int x = settings.X;
            int y = settings.Y;
            // v3.6.8：程序没运行的时候拔掉副屏——配置里的原坐标已完全出屏，EnsureOnScreen 会把它拉回
            // 主屏，而 parked 仍是 false、parkX/parkY 是空值，副屏接回后没有任何线索可归位。
            // 故在夹坐标「之前」先判一次，出屏就直接进入接管态，原坐标留在 parkX/parkY 里等接回。
            int w0 = ClientSize.Width, h0 = ClientSize.Height;
            bool startOffScreen = !IsOnActiveScreen(new Rectangle(settings.X, settings.Y, w0, h0));
            EnsureOnScreen(ref x, ref y);
            Left = x;
            Top = y;
            parked = settings.Parked || startOffScreen;
            parkDevice = settings.ParkDevice ?? "";
            parkMon = settings.ParkMon ?? "";
            parkX = settings.ParkX;
            parkY = settings.ParkY;
            if (startOffScreen && !settings.Parked)
            {
                // 该屏此刻不在，型号信息无从查起，只留原坐标；接回时按坐标找屏（TryRestorePark 的坐标判据）
                parkDevice = "";
                parkMon = "";
                parkX = settings.X;
                parkY = settings.Y;
            }
            
            if (mode == MeterMode.Microphone)
                AudioData.BeginMicMode();
            
            Opacity = userOpacity;
            
            TopMost = settings.AlwaysOnTop;
            alwaysOnTop = settings.AlwaysOnTop;
            
            stealthMode = settings.StealthMode;
            if (stealthMode)
            {
                // 神隐模式启动时默认隐藏
                Opacity = 0;
                silenceFrames = StealthDelayFrames;
                stealthFading = true; // 初始处于隐藏/淡出态，避免被补满逻辑拉回
            }
            else
            {
                stealthFading = false;
            }
            
            UpdateModeCheck();
            UpdateThemeCheck();
            UpdateOpacityCheck();
            UpdateSizeCheck();
            UpdateTopMostCheck();
            UpdateStealthCheck();
        }
        
        // v3.6.4：位置校验
        // v3.6.7：判据统一为「至少露出 40×40」——只露一条边的窗口同样算丢失（与多显示器方案一致）
        private void EnsureOnScreen(ref int x, ref int y)
        {
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            Rectangle r = new Rectangle(x, y, w, h);
            
            bool onScreen = false;
            foreach (Screen s in Screen.AllScreens)
            {
                Rectangle i = Rectangle.Intersect(r, s.Bounds);
                if (i.Width >= 40 && i.Height >= 40) { onScreen = true; break; }
            }
            
            if (!onScreen)
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                x = wa.Left + Math.Max(0, (wa.Width - w) / 2);
                y = wa.Top + Math.Max(0, (wa.Height - h) / 2);
            }
        }
        
        private void SaveCurrentWindow()
        {
            try
            {
                WindowSettings ws = new WindowSettings();
                ws.Id = windowId;
                ws.X = this.Left;
                ws.Y = this.Top;
                ws.Scale = scale;
                ws.Skin = currentSkin;
                ws.Mode = (int)mode;
                ws.Opacity = userOpacity;
                ws.AlwaysOnTop = alwaysOnTop;
                ws.StealthMode = stealthMode;
                // v3.6.7：Park 状态随窗口配置一并持久化
                ws.Parked = parked;
                ws.ParkDevice = parkDevice;
                ws.ParkMon = parkMon;
                ws.ParkX = parkX;
                ws.ParkY = parkY;
                AudioData.SaveWindowSettings(ws);
            }
            catch {}
        }

        // v3.6.2：为新建窗口计算吸附定位——优先右、下、左、上，规避屏幕边缘与已有实例
        private Point ComputeNewWindowLocation(int w, int h, VUMeterForm newForm)
        {
            Rectangle work = Screen.FromControl(this).WorkingArea;
            const int margin = 8;   // 与屏幕边缘保持的安全距离
            int gap = SnapDistance; // 与参照窗口的间距

            // 候选方向：右 → 下 → 左 → 上
            List<Point> candidates = new List<Point>
            {
                new Point(this.Right + gap, this.Top),
                new Point(this.Left, this.Bottom + gap),
                new Point(this.Left - w - gap, this.Top),
                new Point(this.Left, this.Top - h - gap)
            };

            foreach (Point p in candidates)
            {
                Rectangle r = new Rectangle(p.X, p.Y, w, h);

                // 规避屏幕边缘
                if (p.X < margin || p.Y < margin ||
                    p.X + w > work.Right - margin || p.Y + h > work.Bottom - margin)
                    continue;

                // 规避已有实例：候选矩形不得与任何存活窗口相交
                bool overlap = false;
                foreach (var other in AudioData.GetAllWindows())
                {
                    if (other == null || other.IsDisposed || other == this || other == newForm) continue;
                    Rectangle or = new Rectangle(other.Left, other.Top, other.Width, other.Height);
                    if (r.IntersectsWith(or)) { overlap = true; break; }
                }
                if (!overlap) return p;
            }

            // 所有方向都不可用时回退到工作区居中
            return new Point((work.Width - w) / 2, (work.Height - h) / 2);
        }
        
        public void RequestSave()
        {
            if (saveTimer != null)
            {
                saveTimer.Stop();
                saveTimer.Start();
            }
        }
        
        // Win+D不最小化：添加WS_EX_TOOLWINDOW样式，工具窗口不参与"显示桌面"；
        // v3.6.7（T1）：显式剥离 WS_MINIMIZEBOX 兜底，不再单靠 FormBorderStyle.None 的隐式行为；
        // v3.6.7（L2）：加 WS_EX_NOACTIVATE，Show/点击均不抢前台焦点（右键菜单/拖动不受影响）
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                cp.Style &= ~WS_MINIMIZEBOX;
                return cp;
            }
        }

        // v3.6.7（L2）：新建窗口浮现时后台出现，不抢前台焦点
        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }
        
        protected override void WndProc(ref Message m)
        {
            const int WM_QUERYENDSESSION = 0x0011;
            const int WM_ENDSESSION = 0x0016;
            const int WM_DISPLAYCHANGE = 0x007E;

            if (m.Msg == WM_QUERYENDSESSION || m.Msg == WM_ENDSESSION)
            {
                // v3.6.4：与 OnFormClosing 对齐——保留设置退出/彻底退出时不写回
                if (!AudioData.KeepSettingsOnClose && !AudioData.SuppressSaveOnClose)
                {
                    SaveCurrentWindow();
                }
            }

            // v3.6.7：显示器拓扑变化（拔插/分辨率切换/Win+P）——迁屏或接屏归位。
            // WM_DISPLAYCHANGE 直达 UI 线程，无需跨线程调度
            if (m.Msg == WM_DISPLAYCHANGE)
            {
                HandleDisplaysChanged();
            }

            // v3.6：删除 WM_NCHITTEST 分区穿透，全窗口返回 HTCLIENT，
            // 否则上部 2/3 返回 HTTRANSPARENT 会在分层窗口与桌面层之间引发 hit-test 死循环卡死
            base.WndProc(ref m);
        }
        
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            AudioData.EnsureOutputInitialized();
            
            if (mode == MeterMode.Microphone)
                AudioData.BeginMicMode();
            
            // v3.6.6：无 owner 路线——此处只同步置顶标志/工具窗口样式，并把桌面层窗口落位
            InitWindowLayer();

            // v3.6.7：启动时若处于「拔屏接管」状态且原屏已接回，立即归位；随后记住当前所在屏
            if (parked) TryRestorePark();
            RememberScreen();
        }

        // 首帧初始化层级：同步置顶标志与工具窗口样式；桌面层窗口落位到桌面正上方
        private void InitWindowLayer()
        {
            // v3.6.6：同值守卫——WinForms 对未变化的 TopMost 赋值也会发 SetWindowPos(HWND_NOTOPMOST)，
            // 把窗口挪到非置顶 band 顶部，是启动时多余的一次 Z 序扰动
            if (this.TopMost != alwaysOnTop) this.TopMost = alwaysOnTop;
            int exStyle = GetWindowLong(this.Handle, GWL_EXSTYLE);
            SetWindowLong(this.Handle, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
            // v3.6.6：桌面层窗口显式落位（原 owner 模式下由挂载自动落位，现需主动插到桌面正上方）
            if (!alwaysOnTop) ReassertAboveDesktop(this, true);
        }

        // v3.6.6：显示前准备。路线A′+B 已彻底放弃 owner 挂载：
        //  - 不再改 GWL_HWNDPARENT（灰色 DefView owner 是花屏根因；探针B 实测顶层桌面 owner
        //    又无法随抬升的桌面走）；
        //  - Win+D 穿透由 desktopCheckTimer 的重升机制负责（表盘无 WS_MINIMIZEBOX，
        //    Win+D 不会最小化它们，只会被抬升的桌面盖住，检测到后重升即可）；
        //  - 此处仅：强制创建句柄、同步置顶标志、确保工具窗口样式、首帧透明掩盖（防御性保留）。
        // 必须在 Show() 之前调用。
        public void PrepareBeforeShow()
        {
            IntPtr h = this.Handle; // 强制创建隐藏句柄
            if (this.TopMost != alwaysOnTop) this.TopMost = alwaysOnTop;
            int exStyle = GetWindowLong(h, GWL_EXSTYLE);
            SetWindowLong(h, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);

            // 首帧透明：Show 后由 renderTimer 快速淡入到目标透明度。
            // 神隐启动窗口本就隐藏，不参与。
            if (!stealthMode && userOpacity > 0)
            {
                introTargetOpacity = userOpacity;
                this.Opacity = 0;
                introDelayFrames = 3;
                introFading = true;
            }
        }

        // 动态切换窗口层级（托盘菜单手动切换时调用）。
        // v3.6.6：不再改 owner——置顶只切 TopMost；桌面层只把窗口落位到桌面窗口正上方，
        // 全程不触碰 owner 关系，不再触发 band 重排花屏。
        private void ApplyWindowLayer(bool topMost)
        {
            if (topMost)
            {
                this.TopMost = true;
            }
            else
            {
                this.TopMost = false;
                ReassertAboveDesktop(this, true); // 落位桌面层
            }
            // 重新确保WS_EX_TOOLWINDOW样式
            int exStyle = GetWindowLong(this.Handle, GWL_EXSTYLE);
            SetWindowLong(this.Handle, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
        }

        // ---------------- v3.6.6：显示桌面检测 + 重升 ----------------

        // Z 序名次：0 = 最顶层；-1 = 未找到
        private static int ZRankOf(IntPtr target)
        {
            IntPtr h = GetTopWindow(IntPtr.Zero);
            int i = 0;
            while (h != IntPtr.Zero)
            {
                if (h == target) return i;
                h = GetWindow(h, GW_HWNDNEXT);
                i++;
                if (i > 10000) break;
            }
            return -1;
        }

        // 桌面链（Progman / WorkerW 连续段）中「最靠前那扇」；找不到返回 IntPtr.Zero
        // v3.6.8a：原实现取"所有 WorkerW 里 Z 序最高的那扇"，但这台机器上 explorer 常驻
        // 大量与桌面无关的隐藏 WorkerW，它们的 Z 序飘在普通窗口中间——最小名次的 WorkerW
        // 是 rank 86/412 的一扇隐藏窗口，于是表盘被插到第 81 位，压住了资源管理器
        // （rank 394/404）这类"没被激活过"的窗口。改为从 Progman（或 Shell 桌面窗口）
        // 出发沿 Z 序向上爬，只吃同一条连续的桌面链，取链上最上面那扇：表盘正好落在
        // "桌面链顶上、所有普通窗口之下"的最底一格。
        private static string ClassNameOf(IntPtr h)
        {
            if (h == IntPtr.Zero) return "";
            StringBuilder sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static IntPtr TopDesktopWindow()
        {
            IntPtr start = FindWindow("Progman", null);
            if (start == IntPtr.Zero) start = GetShellWindow();
            if (start == IntPtr.Zero) return IntPtr.Zero;

            IntPtr top = start;
            IntPtr up = GetWindow(top, GW_HWNDPREV);
            int guard = 0;
            while (up != IntPtr.Zero && guard++ < 64)
            {
                string cls = ClassNameOf(up);
                if (cls != "WorkerW" && cls != "Progman") break;  // 桌面链断开了，停
                top = up;
                up = GetWindow(top, GW_HWNDPREV);
            }
            return top;
        }

        private static int DesktopTopRank()
        {
            IntPtr w = TopDesktopWindow();
            return w == IntPtr.Zero ? -1 : ZRankOf(w);
        }

        // 单窗口重升：插到"桌面窗口正上方"。
        // force=false：仅当窗口被桌面盖住时才动作（供看门狗轮询使用）；
        // force=true ：无条件落位（供新建窗口 / 层级切换使用）。
        public static void ReassertAboveDesktop(VUMeterForm form, bool force)
        {
            try
            {
                if (form == null || !form.IsHandleCreated) return;
                IntPtr desk = TopDesktopWindow();
                if (desk == IntPtr.Zero) return;
                int dRank = ZRankOf(desk);
                int myRank = ZRankOf(form.Handle);
                if (dRank < 0 || myRank < 0) return;
                if (!force && dRank >= myRank) return; // 桌面在窗口之下（未被盖住），无需处理
                IntPtr insertAfter = GetWindow(desk, GW_HWNDPREV); // 桌面正上方那扇
                if (insertAfter == IntPtr.Zero) return;
                // 插入点若已在置顶 band 内，跳过本轮（避免把表盘带成置顶），看门狗下轮重试
                if ((GetWindowLong(insertAfter, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0) return;
                SetWindowPos(form.Handle, insertAfter, 0, 0, 0, 0, SWP_ZFLAGS);
            }
            catch { }
        }

        // 全体桌面层窗口检查 + 有序重升（desktopCheckTimer 每 200ms 调一次）。
        // 多窗口按当前 Z 序"从底到顶"逐个插入同一插入点，保持彼此相对顺序
        // （后插者紧贴插入点、先插者被依次下推）。
        public static void CheckDesktopWindows()
        {
            try
            {
                List<VUMeterForm> list = new List<VUMeterForm>();
                foreach (VUMeterForm w in AudioData.GetAllWindows())
                {
                    if (w.alwaysOnTop || !w.IsHandleCreated || !w.Visible) continue;
                    list.Add(w);
                }
                if (list.Count == 0) return;

                int dRank = DesktopTopRank();
                if (dRank < 0) return;

                bool needFix = false;
                foreach (VUMeterForm w in list)
                {
                    int r = ZRankOf(w.Handle);
                    if (r >= 0 && dRank < r) { needFix = true; break; }
                }
                if (!needFix) return; // 桌面未抬升（常态），零开销直通

                list.Sort(delegate (VUMeterForm a, VUMeterForm b)
                {
                    return ZRankOf(b.Handle).CompareTo(ZRankOf(a.Handle)); // 从底到顶
                });
                IntPtr insertAfter = GetWindow(TopDesktopWindow(), GW_HWNDPREV);
                if (insertAfter == IntPtr.Zero) return;
                if ((GetWindowLong(insertAfter, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0) return;
                foreach (VUMeterForm w in list)
                {
                    SetWindowPos(w.Handle, insertAfter, 0, 0, 0, 0, SWP_ZFLAGS);
                }
            }
            catch { }
        }
        
        // v3.6.7：Win+D 由无 owner 路线的看门狗重升机制解决，无需 OnResize 兜底

        // ---------------- v3.6.7：多显示器拔插（迁屏 + 重连归位） ----------------

        // 面积最大者胜出；完全不在任何活跃屏上返回 null
        private static Screen ScreenOfWindow(Rectangle wr)
        {
            Screen best = null; long bestArea = 0;
            foreach (Screen sc in Screen.AllScreens)
            {
                Rectangle i = Rectangle.Intersect(wr, sc.Bounds);
                long area = (long)i.Width * i.Height;
                if (area > bestArea) { bestArea = area; best = sc; }
            }
            return bestArea > 0 ? best : null;
        }

        // 是否还在活跃屏上：至少露出 40×40（只露一条边同样算丢失）
        private static bool IsOnActiveScreen(Rectangle wr)
        {
            foreach (Screen sc in Screen.AllScreens)
            {
                Rectangle i = Rectangle.Intersect(wr, sc.Bounds);
                if (i.Width >= 40 && i.Height >= 40) return true;
            }
            return false;
        }

        private static Screen ScreenByDevice(string dev)
        {
            if (string.IsNullOrEmpty(dev)) return null;
            foreach (Screen sc in Screen.AllScreens)
                if (string.Equals(sc.DeviceName, dev, StringComparison.OrdinalIgnoreCase)) return sc;
            return null;
        }

        private static Screen ScreenByMonitor(string monId)
        {
            if (string.IsNullOrEmpty(monId)) return null;
            foreach (Screen sc in Screen.AllScreens)
            {
                string id = MonitorIdOf(sc);
                if (!string.IsNullOrEmpty(id) &&
                    string.Equals(id, monId, StringComparison.OrdinalIgnoreCase)) return sc;
            }
            return null;
        }

        // 取某 DISPLAYn 上物理显示器的 DeviceID（重排编号时归位的副判据）
        private static string MonitorIdOf(Screen sc)
        {
            try
            {
                DISPLAY_DEVICE dd = new DISPLAY_DEVICE();
                dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                if (!EnumDisplayDevices(sc.DeviceName, 0, ref dd, 0)) return "";
                return dd.DeviceID ?? "";
            }
            catch { return ""; }
        }

        // 平时记住「我当前在哪块屏」：已不在任何活跃屏时保持上一个已知值
        private void RememberScreen()
        {
            try
            {
                Screen sc = ScreenOfWindow(new Rectangle(Left, Top, Width, Height));
                if (sc != null)
                {
                    // v3.6.8：DeviceID 要枚举显示器（EnumDisplayDevices），心跳 200ms 一次 ×N 个表盘纯属浪费；
                    // 只有 \\.\DISPLAYn 变化时才重查，其余沿用缓存值
                    if (!string.Equals(lastDev, sc.DeviceName, StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrEmpty(lastMon))
                    {
                        lastDev = sc.DeviceName;
                        lastMon = MonitorIdOf(sc);
                    }
                }
            }
            catch { }
        }

        // 断开 → 先记档（设备名 + DeviceID + 原坐标），再迁到主屏工作区居中。顺序不能反
        private void ParkToActiveScreen()
        {
            try
            {
                Screen cur = ScreenOfWindow(new Rectangle(Left, Top, Width, Height));
                if (!parked)
                {
                    parked = true;
                    parkDevice = cur != null ? cur.DeviceName : lastDev; // 屏已没了，只能靠平时记住的名字
                    parkMon = cur != null ? MonitorIdOf(cur) : lastMon;
                    parkX = Left; parkY = Top;
                }
                Rectangle pb = Screen.PrimaryScreen.WorkingArea; // 用 WorkingArea 避开任务栏
                int x = pb.Left + Math.Max(0, (pb.Width - Width) / 2);
                int y = pb.Top + Math.Max(0, (pb.Height - Height) / 2);
                if (x != Left || y != Top) { Left = x; Top = y; }
                SaveCurrentWindow();
            }
            catch { }
        }

        // 接回 → 先按设备名找，找不到再按 DeviceID 找；都没有就按原坐标反推是哪块屏；都不成继续等（不乱跳）
        private bool TryRestorePark()
        {
            try
            {
                if (!parked) return false;
                Screen sc = ScreenByDevice(parkDevice);
                if (sc == null) sc = ScreenByMonitor(parkMon);
                // v3.6.8：程序没运行期间拔屏，设备名/型号都无从记录（见 ApplySettings 的 startOffScreen 分支），
                // 只剩原坐标——原坐标此刻已经落到某块活跃屏上，说明那块屏就是原屏
                if (sc == null && parkX != int.MinValue && parkY != int.MinValue &&
                    IsOnActiveScreen(new Rectangle(parkX, parkY, Width, Height)))
                {
                    sc = ScreenOfWindow(new Rectangle(parkX, parkY, Width, Height));
                }
                if (sc == null) return false;
                Rectangle pb = sc.WorkingArea;
                int x = parkX, y = parkY;
                if (x == int.MinValue || y == int.MinValue)
                {
                    x = pb.Left + Math.Max(0, (pb.Width - Width) / 2);
                    y = pb.Top + 80;
                }
                // 新屏幕分辨率可能比原来小，夹回该屏工作区内
                x = Math.Max(pb.Left, Math.Min(x, pb.Right - Width));
                y = Math.Max(pb.Top, Math.Min(y, pb.Bottom - Height));
                parked = false;
                // v3.6.8：归位后清掉接管档，避免下次启动又拿旧档案去救（Parked/ParkX 会随配置落盘）
                parkDevice = "";
                parkMon = "";
                parkX = int.MinValue;
                parkY = int.MinValue;
                if (Left != x || Top != y) { Left = x; Top = y; }
                SaveCurrentWindow();
                return true;
            }
            catch { return false; }
        }

        // 拓扑变化统一入口（WM_DISPLAYCHANGE / 看门狗兜底）：
        // 屏还在 → 夹回该屏工作区 + 记屏 + 尝试归位；屏没了 → 迁到主屏居中。
        // 注意：此处只动位置，不动 Z 序——拓扑瞬间桌面层层级在重建，
        // Z 序交由 desktopCheckTimer 看门狗下一拍处理（方案已知边界 5）
        private void HandleDisplaysChanged()
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                Rectangle wr = new Rectangle(Left, Top, Width, Height);
                if (IsOnActiveScreen(wr))
                {
                    Screen sc = ScreenOfWindow(wr);
                    if (sc == null) return;
                    Rectangle pb = sc.WorkingArea;
                    int x = Math.Max(pb.Left, Math.Min(Left, pb.Right - Width));
                    int y = Math.Max(pb.Top, Math.Min(Top, pb.Bottom - Height));
                    if (x != Left || y != Top) { Left = x; Top = y; SaveCurrentWindow(); }
                    RememberScreen();
                    TryRestorePark(); // 原屏回来了（哪怕此刻在别的屏上）也立刻归位
                    return;
                }
                ParkToActiveScreen();
            }
            catch { }
        }

        // 看门狗心跳兜底：刷新各表盘所在屏记忆 + 接屏归位（防事件漏收/休眠错过）
        public static void HeartbeatScreens()
        {
            List<VUMeterForm> list = AudioData.GetAllWindows();
            for (int i = list.Count - 1; i >= 0; i--)
            {
                VUMeterForm w = list[i];
                if (w == null || w.IsDisposed || !w.IsHandleCreated) continue;
                w.RememberScreen();
                if (w.parked) w.TryRestorePark();
            }
        }
        
        private void CreateContextMenu()
        {
            bool zh = (AudioData.CurrentLanguage == "zh");
            contextMenu = new ContextMenuStrip();
            
            // 显示模式
            modeMenu = new ToolStripMenuItem(zh ? "显示模式" : "Display Mode");
            string[] modes = zh ? 
                new string[] { "左声道", "右声道", "总音量", "麦克风输入" } :
                new string[] { "Left Channel", "Right Channel", "Master Volume", "Microphone Input" };
            MeterMode[] modeValues = { MeterMode.Left, MeterMode.Right, MeterMode.Master, MeterMode.Microphone };
            for (int i = 0; i < modes.Length; i++)
            {
                int idx = i;
                ToolStripMenuItem item = new ToolStripMenuItem(modes[i]);
                item.Click += (s, e) => { 
                    MeterMode prevMode = mode;
                    mode = modeValues[idx]; 
                    if (mode == MeterMode.Microphone)
                    {
                        // 本来就在等待、用户又点了同一个 Mic 项：不再重复弹框打扰
                        bool alreadyWaiting = (prevMode == MeterMode.Microphone) &&
                                              (AudioData.MicState == AudioData.MicStatus.Waiting);
                        // v3.6.9：先试开采集；失败不再甩异常文案，改走三按钮对话框
                        if (!AudioData.BeginMicMode() && !alreadyWaiting)
                        {
                            int answer = ShowMicUnavailableDialog();
                            if (answer == 2) // 取消：放弃这次 Mic 设定，恢复到设定前的显示项目
                            {
                                mode = (prevMode == MeterMode.Microphone) ? MeterMode.Master : prevMode;
                            }
                            // 0 确定：已跳系统输入设置，保持 Mic 设定并继续等待
                            // 1 等待接入：保持 Mic 设定，设备接入后自动接管
                        }
                    }
                    // 统一收口：还有窗口要 Mic 就保持，都没人要才释放采集
                    AudioData.RefreshMicDemand();
                    UpdateModeCheck();
                    RequestSave();
                };
                modeMenu.DropDownItems.Add(item);
            }
            contextMenu.Items.Add(modeMenu);

            // v3.6：在"显示模式"下方加入系统音频面板快捷入口
            ToolStripMenuItem mixerItem = new ToolStripMenuItem(zh ? "音量合成器" : "Volume Mixer");
            mixerItem.Click += (s, e) => {
                try { Process.Start(new ProcessStartInfo("sndvol.exe") { UseShellExecute = true }); }
                catch { MessageBox.Show(zh ? "无法打开音量合成器" : "Unable to open Volume Mixer"); }
            };
            contextMenu.Items.Add(mixerItem);

            ToolStripMenuItem soundItem = new ToolStripMenuItem(zh ? "声音设置" : "Sound Settings");
            soundItem.Click += (s, e) => {
                try { Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true }); }
                catch { MessageBox.Show(zh ? "无法打开声音设置" : "Unable to open Sound Settings"); }
            };
            contextMenu.Items.Add(soundItem);

            contextMenu.Items.Add(new ToolStripSeparator());
            
            // 主题（v3.6.2：六套素材全部开放）
            themeMenu = new ToolStripMenuItem(zh ? "主题" : "Theme");
            string[] themes = zh ?
                new string[] { "玄铁圆初", "藏地秘银", "梦幻瑰金", "黑木胡桃", "冷战记忆", "塑料胆机" } :
                new string[] { "Black Iron Classic", "Tibetan Mithril", "Dreamy Rose Gold", "Black Walnut", "Cold War Relic", "Plastic Tube Amp" };
            for (int i = 0; i < themes.Length; i++)
            {
                int idx = i;
                ToolStripMenuItem item = new ToolStripMenuItem(themes[i]);
                item.Enabled = true;
                item.Click += (s, e) => { 
                    currentSkin = idx; 
                    // 切换主题时重新计算窗口尺寸和轴心
                    ClientSize = new Size(
                        (int)(backgroundImages[currentSkin].Width * scale),
                        (int)(backgroundImages[currentSkin].Height * scale)
                    );
                    needleCenterX = backgroundImages[currentSkin].Width / 2f;
                    needleCenterY = backgroundImages[currentSkin].Height * 0.6f + 16f;
                    UpdateThemeCheck(); 
                    RequestSave(); 
                };
                themeMenu.DropDownItems.Add(item);
            }
            contextMenu.Items.Add(themeMenu);
            
            contextMenu.Items.Add(new ToolStripSeparator());
            
            // 大小（180%~60%，10%递进）
            sizeMenu = new ToolStripMenuItem(zh ? "大小" : "Size");
            string[] sizes = { "180%", "170%", "160%", "150%", "140%", "130%", "120%", "110%", "100%", "90%", "80%", "70%", "60%" };
            float[] scales = { 1.8f, 1.7f, 1.6f, 1.5f, 1.4f, 1.3f, 1.2f, 1.1f, 1.0f, 0.9f, 0.8f, 0.7f, 0.6f };
            for (int i = 0; i < sizes.Length; i++)
            {
                int idx = i;
                ToolStripMenuItem item = new ToolStripMenuItem(sizes[i]);
                item.Click += (s, e) => {
                    scale = scales[idx];
                    ClientSize = new Size(
                        (int)(backgroundImages[currentSkin].Width * scale),
                        (int)(backgroundImages[currentSkin].Height * scale)
                    );
                    UpdateSizeCheck();
                    RequestSave();
                };
                sizeMenu.DropDownItems.Add(item);
            }
            contextMenu.Items.Add(sizeMenu);
            
            // 透明度
            opacityMenu = new ToolStripMenuItem(zh ? "透明度" : "Opacity");
            string[] opacities = { "100%", "90%", "80%", "70%", "60%", "50%", "40%", "30%", "20%" };
            double[] opacityValues = { 1.0, 0.9, 0.8, 0.7, 0.6, 0.5, 0.4, 0.3, 0.2 };
            for (int i = 0; i < opacities.Length; i++)
            {
                int idx = i;
                ToolStripMenuItem item = new ToolStripMenuItem(opacities[i]);
                item.Click += (s, e) => {
                    userOpacity = opacityValues[idx];
                    // v3.6：神隐模式下也实时应用透明度——如果新值低于当前 Opacity 直接降下来，
                    // 如果高于当前 Opacity 由主 timer 自动淡入
                    Opacity = opacityValues[idx];
                    UpdateOpacityCheck();
                    RequestSave();
                };
                opacityMenu.DropDownItems.Add(item);
            }
            contextMenu.Items.Add(opacityMenu);
            
            // 神隐模式
            stealthMenuItem = new ToolStripMenuItem(zh ? 
                (stealthMode ? "关闭神隐模式" : "开启神隐模式") : 
                "Stealth Mode");
            stealthMenuItem.Click += (s, e) => {
                if (stealthMode)
                {
                    stealthMode = false;
                    Opacity = userOpacity;
                    silenceFrames = 0;
                    stealthFading = false;
                    UpdateStealthCheck();
                    RequestSave();
                }
                else
                {
                    ShowStealthWarning();
                }
            };
            contextMenu.Items.Add(stealthMenuItem);
            
            contextMenu.Items.Add(new ToolStripSeparator());
            
            // 窗口层级
            topMostMenu = new ToolStripMenuItem(zh ? "窗口层级" : "Window Layer");
            ToolStripMenuItem topItem = new ToolStripMenuItem(zh ? "始终置顶" : "Always On Top");
            topItem.Click += (s, e) => { alwaysOnTop = true; ApplyWindowLayer(true); UpdateTopMostCheck(); RequestSave(); };
            ToolStripMenuItem bottomItem = new ToolStripMenuItem(zh ? "桌面层（可被遮挡）" : "Desktop Layer (Can Be Covered)");
            bottomItem.Click += (s, e) => { alwaysOnTop = false; ApplyWindowLayer(false); UpdateTopMostCheck(); RequestSave(); };
            topMostMenu.DropDownItems.Add(topItem);
            topMostMenu.DropDownItems.Add(bottomItem);
            contextMenu.Items.Add(topMostMenu);
            
            contextMenu.Items.Add(new ToolStripSeparator());
            
            // 新建 VU 表
            ToolStripMenuItem newItem = new ToolStripMenuItem(zh ? "新建 VU 表" : "New VU Meter");
            newItem.Click += (s, e) => {
                VUMeterForm newForm = new VUMeterForm();
                // v3.6.2：继承当前右键窗口的大小、透明度、主题
                newForm.scale = this.scale;
                newForm.currentSkin = this.currentSkin;
                newForm.userOpacity = this.userOpacity;
                newForm.Opacity = this.userOpacity;
                newForm.UpdateOpacityCheck();
                // v3.6.7（L1）：新建窗口继承声道模式 / 置顶状态 / 神隐模式
                newForm.mode = this.mode;
                if (newForm.mode == MeterMode.Microphone) AudioData.BeginMicMode();
                newForm.UpdateModeCheck();
                newForm.alwaysOnTop = this.alwaysOnTop;
                newForm.UpdateTopMostCheck();
                if (this.stealthMode)
                {
                    // 继承后先保持可见，待静音后再进入淡出（与手动开启神隐的时序一致）
                    newForm.stealthMode = true;
                    newForm.silenceFrames = 0;
                    newForm.stealthFading = false;
                    newForm.UpdateStealthCheck();
                }
                // 按继承的主题/比例重算窗口尺寸与轴心
                newForm.ClientSize = new Size(
                    (int)(backgroundImages[newForm.currentSkin].Width * newForm.scale),
                    (int)(backgroundImages[newForm.currentSkin].Height * newForm.scale));
                newForm.needleCenterX = backgroundImages[newForm.currentSkin].Width / 2f;
                newForm.needleCenterY = backgroundImages[newForm.currentSkin].Height * 0.6f + 16f;
                // v3.6.4：补设窗口标题——原版新建窗口 Text 为空，单实例激活找不到窗口
                newForm.Text = "VU Widgets v3.6.9";
                // 吸附定位：左右或上下方向，规避屏幕边缘与已有实例
                Point loc = ComputeNewWindowLocation(newForm.Width, newForm.Height, newForm);
                newForm.StartPosition = FormStartPosition.Manual;
                newForm.Location = loc;
                newForm.RequestSave();
                // 显示前同步层级与样式并保持首帧透明，避免可见后再调整触发重排闪屏
                newForm.PrepareBeforeShow();
                newForm.Show();
            };
            contextMenu.Items.Add(newItem);
            
            // 注意事项
            ToolStripMenuItem aboutItem = new ToolStripMenuItem(zh ? "注意事项" : "Important Notes");
            aboutItem.Click += (s, e) => { ShowAbout(); };
            contextMenu.Items.Add(aboutItem);
            
            // 语言切换：中文界面显示English，英文界面显示中文
            ToolStripMenuItem langItem = new ToolStripMenuItem(zh ? "English" : "中文");
            langItem.Click += (s, e) => {
                AudioData.SetLanguage(zh ? "en" : "zh");
                // v3.6.4：释放旧菜单，修复语言反复切换时的菜单泄漏
                ContextMenuStrip = null;
                ContextMenuStrip oldMenu = contextMenu;
                CreateContextMenu();
                ContextMenuStrip = contextMenu;
                try { if (oldMenu != null) oldMenu.Dispose(); } catch { }
            };
            contextMenu.Items.Add(langItem);
            
            contextMenu.Items.Add(new ToolStripSeparator());
            
            // 关闭
            ToolStripMenuItem closeItem = new ToolStripMenuItem(zh ? "关闭" : "Close");
            closeItem.Click += (s, e) => { Close(); };
            contextMenu.Items.Add(closeItem);
            
            UpdateModeCheck();
            UpdateThemeCheck();
            UpdateOpacityCheck();
            UpdateSizeCheck();
            UpdateTopMostCheck();
            UpdateStealthCheck();
        }
        
        private void ShowStealthWarning()
        {
            bool zh = (AudioData.CurrentLanguage == "zh");
            // v3.6.4：using 确保对话框 Form 释放
            // v3.6.5：对话框内 Font 一并 using 释放，避免每次打开泄漏 GDI 字体句柄
            using (Form warning = new Form())
            using (Font f9 = new Font("微软雅黑", 9))
            using (Font f10 = new Font("微软雅黑", 10))
            {
                warning.Text = zh ? "神隐模式开启确认" : "Stealth Mode Confirmation";
                warning.FormBorderStyle = FormBorderStyle.FixedDialog;
                warning.StartPosition = FormStartPosition.CenterScreen;
                warning.MaximizeBox = false;
                warning.MinimizeBox = false;
                warning.Width = zh ? 480 : 600;
                warning.Height = 448;
                warning.Font = f9;
                
                Label lbl = new Label();
                if (zh)
                {
                    lbl.Text = "★注意：\"神隐模式\"开启后，桌面VU表仅在检测到对应音频信号时才会现身，静音或无音频信号时呈隐藏不可见状态，鼠标操作在隐藏不可见期间无效，如遇系统或程序故障，必须通过系统托盘内的VU Widgets图标使其显形或退出神隐模式";
                }
                else
                {
                    lbl.Text = "★Note: When \"Stealth Mode\" is enabled, the desktop VU meter only appears when the corresponding audio signal is detected. It remains hidden and invisible when muted or no audio signal is present. Mouse operations are ineffective while hidden. In case of system or program failure, you must use the VU Widgets icon in the system tray to make it visible or exit Stealth Mode.";
                }
                lbl.Location = new Point(20, 15);
                lbl.Size = zh ? new Size(430, 310) : new Size(550, 310);
                lbl.TextAlign = ContentAlignment.TopLeft;
                lbl.Font = f10;
                warning.Controls.Add(lbl);
                
                Button btnOk = new Button();
                btnOk.Text = zh ? "确定" : "Confirm";
                btnOk.Location = zh ? new Point(268, 340) : new Point(370, 340);
                btnOk.Size = zh ? new Size(88, 33) : new Size(106, 33);
                btnOk.DialogResult = DialogResult.OK;
                warning.Controls.Add(btnOk);
                
                Button btnCancel = new Button();
                btnCancel.Text = zh ? "取消" : "Cancel";
                btnCancel.Location = zh ? new Point(368, 340) : new Point(488, 340);
                btnCancel.Size = new Size(88, 33);
                btnCancel.DialogResult = DialogResult.Cancel;
                warning.Controls.Add(btnCancel);
                
                warning.AcceptButton = btnOk;
                warning.CancelButton = btnCancel;
                
                if (warning.ShowDialog() == DialogResult.OK)
                {
                    stealthMode = true;
                    silenceFrames = 0;
                    stealthFading = false; // 刚开启神隐，保持可见，待静音后再进入淡出
                    Opacity = userOpacity;
                    UpdateStealthCheck();
                    RequestSave();
                }
            }
        }
        
        // ============================================================================
        // v3.6.8：关于 / 注意事项（托盘右键"关于"与表盘右键"注意事项"共用同一份内容）
        //  需求②：文案换成用户定稿的四段 + 落款，中英文都必须"一屏显示全部内容"（无滚动条）
        //  需求③：༺ ༻（U+0F3A / U+0F3B）在部分系统缺字形（微软雅黑不含这两个码位，靠字体回退
        //         才碰巧能显示），故在构建期从可正常显示的字库（Microsoft Himalaya）抓取字形，
        //         存成透明底 PNG（mark_open.png / mark_close.png）随 exe 内嵌；运行时用位图绘制
        //         纹饰、用正常字体绘制姓名，任何系统都不会再出现豆腐块。
        //  排版：不再用 TextBox。TextBox 的实际换行与 TextRenderer.MeasureText 不一致，
        //         3.6.7 的 787/984×756 固定像素在高 DPI（本机 175%）下内容会被裁切。
        //         这里改为自绘（NoticeView），窗口尺寸由排版结果反算，字号在 10pt→7pt 间自动降级。
        // ============================================================================
        private const string ABOUT_MARK_TOKEN = "{NAME}";
        private static readonly string ABOUT_NAME_ZH = "格桑仁青";
        // v3.6.8a：英文版不再做拉丁转写，仍按中文「格桑仁青」显示（姓名保持原名更准确）
        private static readonly string ABOUT_NAME_EN = "格桑仁青";

        // 段间空一行；第 4 段与落款之间空两行
        private const string ABOUT_TEXT_ZH =
            "★VU表桌面小工具（Windows Widgets）是绿色免安装程序，适用Windows 10/11系统，更早系统版本可能需要额外安装NET Framework 4.x方可支持；\n\n" +
            "★清除本程序无需卸载，但删除程序执行文件前请务必先在系统托盘图标单击右键，选择\"消除一切设置彻底退出\"，以免存在系统残留；\n\n" +
            "★强烈推荐\"神隐模式\"，开启该模式前请务必仔细阅读弹出的告知内容；\n\n" +
            "★此程序是{NAME} & Old Newbie 借助豆包专业版及Codebuddy编写的免费小工具，仅限非商业用途的学习、测试，其指示阈值不具备任何专业性，作者对其使用过程中出现的任何问题不予负责且不承诺后续版本的升级。此程序主题美工借鉴了\"酷鱼桌面/酷鱼小工具/XWidgets\"中的VU系列，特此鸣谢酷鱼和原作者Jimking\n\n\n" +
            "2026年11月·四川省甘孜藏族自治州";

        private const string ABOUT_TEXT_EN =
            "★VU Widgets (Windows Widgets) is a portable, installation-free utility for Windows 10/11. Earlier Windows versions may require an additional .NET Framework 4.x installation;\n\n" +
            "★No uninstallation is needed to remove this program. However, before deleting the executable, please right-click the program icon in the system tray and choose \"Clear All Settings & Exit\" first, so that no settings are left behind on the system;\n\n" +
            "★\"Stealth Mode\" is strongly recommended. Before enabling it, please read the notice that pops up carefully;\n\n" +
            "★This program is a free widget written by {NAME} & Old Newbie with the help of Doubao Pro and Codebuddy. It is for non-commercial study and testing only; its indicator thresholds carry no professional authority. The author accepts no responsibility for any problem arising from its use and makes no commitment to future version upgrades. The theme artwork draws on the VU series of \"Ku-yu Desktop / Ku-yu Widgets / XWidgets\"; special thanks to Ku-yu and to the original author, Jimking\n\n\n" +
            "November 2026 · Garzê Tibetan Autonomous Prefecture, Sichuan, China";

        // v3.6.7：public static——托盘右键"关于"菜单复用本对话框
        // v3.6.8：改为自绘 + 自适应尺寸 + 纹饰字形位图
        public static void ShowAbout()
        {
            bool zh = (AudioData.CurrentLanguage == "zh");
            string body = zh ? ABOUT_TEXT_ZH : ABOUT_TEXT_EN;
            string name = zh ? ABOUT_NAME_ZH : ABOUT_NAME_EN;

            Bitmap markOpen = LoadAboutMark("VolumeMeter.mark_open.png");
            Bitmap markClose = LoadAboutMark("VolumeMeter.mark_close.png");
            if (markOpen == null || markClose == null)
                name = "༺" + name + "༻";   // 资源缺失时的降级：退回文字（缺字形的系统会显示豆腐块，但不至于空白）

            Graphics gRef = null;
            Font font = null;
            int dpi = 96;
            int textH = 0, lineH = 0, nameW = 0;
            try
            {
                gRef = Graphics.FromHwnd(IntPtr.Zero);
                dpi = (int)Math.Round(gRef.DpiY);
            }
            catch { }

            float k = dpi / 96f;
            // v3.6.8：按「光标所在屏」的工作区排版并居中——表盘可能停在副屏/竖屏上，
            // 用主屏尺寸去算会把副屏的弹窗顶到屏幕外
            Rectangle wa;
            try { wa = Screen.FromPoint(Cursor.Position).WorkingArea; }
            catch { wa = Screen.PrimaryScreen.WorkingArea; }
            int pad = (int)Math.Round(16 * k);
            int btnH = (int)Math.Round(30 * k);
            int btnGap = (int)Math.Round(14 * k);
            int maxW = (int)(wa.Width * 0.92);
            int maxH = (int)(wa.Height * 0.92);

            // 版面宽度：760 逻辑像素封顶，且不吃掉工作区 92% 的边距
            int contentW = Math.Min((int)Math.Round(760 * k), Math.Max(240, maxW - pad * 2));

            try
            {
                // 字号降级：10pt 起，每档 0.5pt，直到「正文 + 内边距 + 按钮」全部塞进工作区 92%
                for (float size = 10f; size >= 7f; size -= 0.5f)
                {
                    Font cand = new Font("微软雅黑", size);
                    MeasureAbout(gRef, cand, body, name, markOpen, contentW, out textH, out lineH, out nameW);
                    if (textH + pad * 2 + btnGap + btnH <= maxH) { font = cand; break; }
                    cand.Dispose();
                }
                if (font == null)
                {
                    // 屏幕小到 7pt 也放不下：仍取 7pt，窗口按内容撑开（宁可窗口略高，也不出现滚动条）
                    font = new Font("微软雅黑", 7f);
                    MeasureAbout(gRef, font, body, name, markOpen, contentW, out textH, out lineH, out nameW);
                }

                int w = contentW + pad * 2;
                int h = textH + pad * 2 + btnGap + btnH;

                Form about = new Form();
                try
                {
                    about.Text = zh ? "注意事项" : "Important Notes";
                    about.FormBorderStyle = FormBorderStyle.FixedDialog;
                    about.StartPosition = FormStartPosition.Manual;
                    about.MaximizeBox = false;
                    about.MinimizeBox = false;
                    about.ShowInTaskbar = true;
                    // 关掉 WinForms 的字体自动缩放，否则 175% DPI 下子控件尺寸会被二次放大，排版全乱
                    about.AutoScaleMode = AutoScaleMode.None;
                    about.ClientSize = new Size(w, h);
                    about.BackColor = SystemColors.Control;
                    // 在「光标所在屏」的工作区内居中（含边框尺寸），并保证不越出该屏
                    about.Location = new Point(
                        Math.Max(wa.Left, wa.Left + (wa.Width - about.Width) / 2),
                        Math.Max(wa.Top, wa.Top + (wa.Height - about.Height) / 2));

                    NoticeView view = new NoticeView();
                    view.TextFont = font;
                    view.Body = body;
                    view.NameText = name;
                    view.MarkOpen = markOpen;
                    view.MarkClose = markClose;
                    view.Location = new Point(pad, pad);
                    view.Size = new Size(contentW, textH);
                    about.Controls.Add(view);

                    Button btnOk = new Button();
                    btnOk.Text = zh ? "知道了" : "Got it";
                    btnOk.Size = new Size((int)Math.Round(104 * k), btnH);
                    btnOk.Location = new Point((w - btnOk.Width) / 2, pad + textH + btnGap);
                    btnOk.DialogResult = DialogResult.OK;
                    about.Controls.Add(btnOk);

                    about.AcceptButton = btnOk;
                    about.ActiveControl = btnOk;
                    about.ShowDialog();
                }
                finally
                {
                    about.Dispose();
                }
            }
            finally
            {
                if (font != null) font.Dispose();
                if (gRef != null) gRef.Dispose();
                if (markOpen != null) markOpen.Dispose();
                if (markClose != null) markClose.Dispose();
            }
        }

        // ========== v3.6.9：Mic 无有效输入的提示框 ==========
        // 返回 0 = 确定（已跳转系统输入设置）；1 = 等待接入（保持 Mic 设定，等设备接入）；
        //      2 = 取消（放弃这次 Mic 设定，由调用方回滚到设定前的显示项目）
        // 文案固定为「目前系统缺乏有效的麦克风输入，是否需要前往输入设置？」，
        // 不再把 InvalidCastException / COMException 之类的技术文案甩给用户（3.6.8 及以前就是这么干的）。
        private static int ShowMicUnavailableDialog()
        {
            bool zh = (AudioData.CurrentLanguage == "zh");
            string body = zh
                ? "目前系统缺乏有效的麦克风输入，是否需要前往输入设置？"
                : "No usable microphone input is available.\nDo you want to open the input settings?";
            string tOk = zh ? "确定" : "OK";
            string tCancel = zh ? "取消" : "Cancel";
            string tWait = zh ? "等待接入" : "Wait for device";

            Graphics gRef = null;
            Font font = null;
            int dpi = 96;
            try { gRef = Graphics.FromHwnd(IntPtr.Zero); dpi = (int)Math.Round(gRef.DpiY); } catch { }
            float k = dpi / 96f;

            Rectangle wa;
            try { wa = Screen.FromPoint(Cursor.Position).WorkingArea; }
            catch { wa = Screen.PrimaryScreen.WorkingArea; }

            int pad = (int)Math.Round(18 * k);
            int btnH = (int)Math.Round(30 * k);
            int btnGapY = (int)Math.Round(14 * k);
            int btnGapX = (int)Math.Round(10 * k);
            int maxW = (int)(wa.Width * 0.92);
            int maxH = (int)(wa.Height * 0.92);
            int contentW = Math.Min((int)Math.Round(520 * k), Math.Max(200, maxW - pad * 2));

            Form dlg = null;
            try
            {
                int textH = 0;
                // 字号降级：10pt 起每档 0.5pt，直到「正文 + 内边距 + 按钮行」全部塞进工作区 92%
                for (float size = 10f; size >= 8f; size -= 0.5f)
                {
                    Font cand = new Font("微软雅黑", size);
                    Size sz = TextRenderer.MeasureText(gRef, body, cand, new Size(contentW, int.MaxValue),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoClipping);
                    if (sz.Height + pad * 2 + btnGapY + btnH <= maxH) { font = cand; textH = sz.Height; break; }
                    cand.Dispose();
                }
                if (font == null)
                {
                    font = new Font("微软雅黑", 8f);
                    Size sz = TextRenderer.MeasureText(gRef, body, font, new Size(contentW, int.MaxValue),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoClipping);
                    textH = sz.Height;
                }

                int bOk = BtnWidth(gRef, font, tOk, k);
                int bCancel = BtnWidth(gRef, font, tCancel, k);
                int bWait = BtnWidth(gRef, font, tWait, k);
                int btnsW = bOk + bCancel + bWait + btnGapX * 2;
                int w = Math.Max(contentW + pad * 2, btnsW + pad * 2);
                int h = textH + pad * 2 + btnGapY + btnH;

                dlg = new Form();
                dlg.Text = zh ? "麦克风输入" : "Microphone Input";
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.StartPosition = FormStartPosition.Manual;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.ShowInTaskbar = true;
                dlg.AutoScaleMode = AutoScaleMode.None;   // 175% DPI 下禁用二次缩放，排版才准
                dlg.ClientSize = new Size(w, h);
                dlg.BackColor = SystemColors.Control;
                dlg.Location = new Point(
                    Math.Max(wa.Left, wa.Left + (wa.Width - dlg.Width) / 2),
                    Math.Max(wa.Top, wa.Top + (wa.Height - dlg.Height) / 2));

                Label lbl = new Label();
                lbl.Text = body;
                lbl.Font = font;
                lbl.AutoSize = false;
                lbl.TextAlign = ContentAlignment.TopLeft;
                lbl.Location = new Point(pad, pad);
                lbl.Size = new Size(contentW, textH);
                dlg.Controls.Add(lbl);

                // 按钮自左向右：确定、取消、等待接入（与用户给定顺序一致）
                int bx = pad + (w - pad * 2 - btnsW) / 2;
                int by = pad + textH + btnGapY;

                Button b1 = new Button();
                b1.Text = tOk;
                b1.Font = font;
                b1.Size = new Size(bOk, btnH);
                b1.Location = new Point(bx, by);
                b1.DialogResult = DialogResult.OK;
                dlg.Controls.Add(b1);

                Button b2 = new Button();
                b2.Text = tCancel;
                b2.Font = font;
                b2.Size = new Size(bCancel, btnH);
                b2.Location = new Point(bx + bOk + btnGapX, by);
                b2.DialogResult = DialogResult.Cancel;
                dlg.Controls.Add(b2);

                Button b3 = new Button();
                b3.Text = tWait;
                b3.Font = font;
                b3.Size = new Size(bWait, btnH);
                b3.Location = new Point(bx + bOk + btnGapX + bCancel + btnGapX, by);
                b3.DialogResult = DialogResult.Yes;   // Yes 表示「等待接入」
                dlg.Controls.Add(b3);

                // 回车默认落在「等待接入」（最不打扰的一项），Esc 等同「取消」
                dlg.AcceptButton = b3;
                dlg.CancelButton = b2;
                dlg.ActiveControl = b3;

                DialogResult r = dlg.ShowDialog();
                if (r == DialogResult.OK)
                {
                    OpenMicInputSettings();
                    return 0;
                }
                if (r == DialogResult.Yes) return 1;
                return 2;
            }
            finally
            {
                if (font != null) font.Dispose();
                if (gRef != null) gRef.Dispose();
                if (dlg != null) dlg.Dispose();
            }
        }

        private static int BtnWidth(Graphics g, Font f, string text, float k)
        {
            Size sz = TextRenderer.MeasureText(g, text, f, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            int wanted = sz.Width + (int)Math.Round(30 * k);
            int min = (int)Math.Round(86 * k);
            return Math.Max(min, wanted);
        }

        // 「确定」按失败类型分流：权限被拒 → 隐私-麦克风；其余（无端点/端点打不开）→ 声音设置
        private static void OpenMicInputSettings()
        {
            string uri = AudioData.MicFailIsPermission ? "ms-settings:privacy-microphone" : "ms-settings:sound";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(uri);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch { }
        }

        private static Bitmap LoadAboutMark(string resName)
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resName))
                {
                    if (s == null) return null;
                    using (Bitmap tmp = new Bitmap(s))
                    {
                        // 复制一份带 Alpha 的独立位图，脱离 Stream 生命周期
                        Bitmap bmp = new Bitmap(tmp.Width, tmp.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        using (Graphics g = Graphics.FromImage(bmp))
                        {
                            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                            g.DrawImage(tmp, 0, 0, tmp.Width, tmp.Height);
                        }
                        return bmp;
                    }
                }
            }
            catch { return null; }
        }

        // 测量与绘制必须共用同一套尺寸口径，否则窗口高度会算不准
        private static void MeasureAbout(Graphics g, Font f, string body, string name, Bitmap mo, int contentW,
                                         out int textH, out int lineH, out int nameW)
        {
            int[] m = NoticeView.Metrics(g, f, name, mo);
            nameW = m[4];
            lineH = (int)Math.Ceiling(f.GetHeight(g));
            textH = NoticeView.MeasureBody(g, body, f, contentW, nameW, lineH);
        }

        // v3.6.8：关于/注意事项正文自绘控件
        //  - 正文用 TextRenderer（GDI）绘制，与系统字体一致，且比 TextBox 的换行可控
        //  - {NAME} 处拆成「左纹饰位图 + 姓名 + 右纹饰位图」三段绘制，纹饰来自内嵌字形位图
        //  - 中英混排断行：连续 ASCII 视作一个整体（西文按单词换行），CJK 逐字可断
        private sealed class NoticeView : Control
        {
            public string Body = "";
            public string NameText = "";
            public Bitmap MarkOpen;
            public Bitmap MarkClose;
            public Font TextFont;

            private sealed class Seg
            {
                public string S;
                public bool Img;
                public int W;
            }

            private sealed class Line
            {
                public List<Seg> Segs = new List<Seg>();
                public int W;
            }

            public NoticeView()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                BackColor = SystemColors.Control;
            }

            // v3.6.8a：必须用「绘制时的那个 DC」去测——TextRenderer.MeasureText 的无 DC 重载
            // 走的是进程的缓存屏幕 DC，实测比真实绘制宽 14px/4 字（110 vs 96），
            // 于是"青"与右纹饰之间平白多出一格空白。换成带 IDeviceContext 的重载后，
            // 测量宽度与 GDI 实际推进宽度完全一致。
            internal static int MeasureW(Graphics g, string s, Font f)
            {
                if (s == null || s.Length == 0) return 0;
                return TextRenderer.MeasureText(g, s, f, new Size(10000, 10000),
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.NoClipping).Width;
            }

            // 返回：{纹饰高, 纹饰宽, 纹饰与姓名间距, 姓名单宽, "纹饰+姓名+纹饰"总宽}
            internal static int[] Metrics(Graphics g, Font f, string name, Bitmap mo)
            {
                float em = f.Size * g.DpiY / 72f;
                if (mo == null)
                {
                    int tw0 = MeasureW(g, name, f);
                    return new int[] { 0, 0, 0, tw0, tw0 };
                }
                int markH = (int)Math.Round(em * 0.72f);
                int markW = (int)Math.Round(markH * (mo.Width * 1f / mo.Height));
                int gap = (int)Math.Round(em * 0.18f);
                int tw = MeasureW(g, name, f);
                return new int[] { markH, markW, gap, tw, markW * 2 + gap * 2 + tw };
            }

            private static List<Seg> Tokenize(string para)
            {
                List<Seg> list = new List<Seg>();
                int i = 0;
                while (i < para.Length)
                {
                    if (string.CompareOrdinal(para, i, ABOUT_MARK_TOKEN, 0, ABOUT_MARK_TOKEN.Length) == 0)
                    {
                        list.Add(new Seg { Img = true });
                        i += ABOUT_MARK_TOKEN.Length;
                        continue;
                    }
                    char c = para[i];
                    if (c < 0x2E80 && c != ' ')
                    {
                        // 连续西文/数字/符号作为一个词，避免在单词中间断行
                        int j = i;
                        while (j < para.Length && para[j] < 0x2E80 && para[j] != ' ' &&
                               string.CompareOrdinal(para, j, ABOUT_MARK_TOKEN, 0, ABOUT_MARK_TOKEN.Length) != 0) j++;
                        list.Add(new Seg { S = para.Substring(i, j - i) });
                        i = j;
                    }
                    else
                    {
                        list.Add(new Seg { S = para.Substring(i, 1) });
                        i++;
                    }
                }
                return list;
            }

            private static void FlushRun(Line cur, ref string run, ref int runW)
            {
                if (run.Length > 0)
                {
                    cur.Segs.Add(new Seg { S = run, W = runW });
                    run = "";
                    runW = 0;
                }
            }

            private static void PushRun(Line cur, ref string run, ref int runW, List<Line> lines)
            {
                FlushRun(cur, ref run, ref runW);
                lines.Add(cur);
            }

            // 禁则：不能出现在行首的收尾标点
            private const string NO_START_CHARS = "，。、；：！？）》」』】”’,;:!?";

            private static List<Line> BuildLines(Graphics g, string body, Font f, int maxW, int nameW)
            {
                List<Line> lines = new List<Line>();
                string[] paras = body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                foreach (string para in paras)
                {
                    if (para.Length == 0) { lines.Add(new Line()); continue; }   // 空行：段间留白
                    Line cur = new Line();
                    string run = "";
                    int runW = 0;
                    foreach (Seg t in Tokenize(para))
                    {
                        if (t.Img)
                        {
                            if (cur.W + nameW > maxW && (cur.Segs.Count > 0 || run.Length > 0))
                            {
                                PushRun(cur, ref run, ref runW, lines);
                                cur = new Line();
                            }
                            FlushRun(cur, ref run, ref runW);
                            cur.Segs.Add(new Seg { Img = true, W = nameW });
                            cur.W += nameW;
                            continue;
                        }
                        if (t.S == " " && run.Length == 0 && cur.Segs.Count == 0) continue;   // 行首不放空格
                        string cand = run + t.S;
                        int candW = MeasureW(g, cand, f);
                        if (cur.W - runW + candW > maxW && (cur.Segs.Count > 0 || run.Length > 0))
                        {
                            // 禁则：收尾标点不能落在行首（否则会看到某行以"，"或"。"开头）。
                            // 遇到这种情况，把前一个字连带标点一起挪到下一行。
                            string carry = "";
                            if (NO_START_CHARS.IndexOf(t.S) >= 0 && run.Length > 0)
                            {
                                carry = run.Substring(run.Length - 1);
                                int oldRunW = runW;
                                run = run.Substring(0, run.Length - 1);
                                runW = run.Length > 0 ? MeasureW(g, run, f) : 0;
                                cur.W -= oldRunW - runW;
                            }
                            PushRun(cur, ref run, ref runW, lines);
                            cur = new Line();
                            if (t.S == " ") continue;
                            cand = carry + t.S;
                            candW = MeasureW(g, cand, f);
                        }
                        run = cand;
                        cur.W += candW - runW;
                        runW = candW;
                    }
                    PushRun(cur, ref run, ref runW, lines);
                }
                return lines;
            }

            internal static int MeasureBody(Graphics g, string body, Font f, int maxW, int nameW, int lineH)
            {
                return BuildLines(g, body, f, maxW, nameW).Count * lineH;
            }

            // 测「格」字的墨迹上下偏移：纹饰按文字墨迹居中，避免上下错位
            private static int[] InkOffsets(Graphics g, Font f, char c)
            {
                int em = (int)Math.Ceiling(f.Size * g.DpiY / 72f) * 2;
                using (Bitmap b = new Bitmap(em + 16, em + 16))
                {
                    using (Graphics gg = Graphics.FromImage(b))
                    {
                        gg.Clear(Color.White);
                        TextRenderer.DrawText(gg, c.ToString(), f, new Point(8, 8), Color.Black,
                            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    }
                    int minY = int.MaxValue, maxY = -1;
                    for (int y = 0; y < b.Height; y++)
                        for (int x = 0; x < b.Width; x++)
                            if (b.GetPixel(x, y).R < 128) { if (y < minY) minY = y; if (y > maxY) maxY = y; }
                    return new int[] { minY - 8, maxY - 8 };
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                if (TextFont == null) return;
                e.Graphics.Clear(BackColor);
                int[] m = Metrics(e.Graphics, TextFont, NameText, MarkOpen);
                int markH = m[0], markW = m[1], gap = m[2], nameW = m[4];
                int lineH = (int)Math.Ceiling(TextFont.GetHeight(e.Graphics));
                int markTop = 0;
                if (MarkOpen != null)
                {
                    int[] ink = InkOffsets(e.Graphics, TextFont, '格');
                    int inkH = ink[1] - ink[0] + 1;
                    markTop = ink[0] + (inkH - markH) / 2;   // 纹饰与汉字墨迹垂直居中对齐
                }
                TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

                List<Line> lines = BuildLines(e.Graphics, Body, TextFont, Width, nameW);
                int y = 0;
                foreach (Line ln in lines)
                {
                    int x = 0;
                    foreach (Seg s in ln.Segs)
                    {
                        if (s.Img)
                        {
                            if (MarkOpen != null)
                            {
                                e.Graphics.DrawImage(MarkOpen, new Rectangle(x, y + markTop, markW, markH));
                                x += markW + gap;
                                TextRenderer.DrawText(e.Graphics, NameText, TextFont, new Point(x, y), ForeColor, flags);
                                x += m[3] + gap;
                                e.Graphics.DrawImage(MarkClose, new Rectangle(x, y + markTop, markW, markH));
                                x += markW;
                            }
                            else
                            {
                                TextRenderer.DrawText(e.Graphics, NameText, TextFont, new Point(x, y), ForeColor, flags);
                                x += nameW;
                            }
                        }
                        else
                        {
                            TextRenderer.DrawText(e.Graphics, s.S, TextFont, new Point(x, y), ForeColor, flags);
                            x += s.W;
                        }
                    }
                    y += lineH;
                }
            }
        }
        
        private void UpdateModeCheck()
        {
            foreach (ToolStripMenuItem item in modeMenu.DropDownItems)
                item.Checked = false;
            if (mode == MeterMode.Left) ((ToolStripMenuItem)modeMenu.DropDownItems[0]).Checked = true;
            if (mode == MeterMode.Right) ((ToolStripMenuItem)modeMenu.DropDownItems[1]).Checked = true;
            if (mode == MeterMode.Master) ((ToolStripMenuItem)modeMenu.DropDownItems[2]).Checked = true;
            if (mode == MeterMode.Microphone) ((ToolStripMenuItem)modeMenu.DropDownItems[3]).Checked = true;
        }
        
        private void UpdateThemeCheck()
        {
            foreach (ToolStripMenuItem item in themeMenu.DropDownItems)
                item.Checked = false;
            ((ToolStripMenuItem)themeMenu.DropDownItems[currentSkin]).Checked = true;
        }
        
        private void UpdateOpacityCheck()
        {
            foreach (ToolStripMenuItem item in opacityMenu.DropDownItems)
                item.Checked = false;
            
            double op = userOpacity;
            double[] opacityValues = { 1.0, 0.9, 0.8, 0.7, 0.6, 0.5, 0.4, 0.3, 0.2 };
            
            int closest = 0;
            double minDiff = Math.Abs(op - opacityValues[0]);
            for (int i = 1; i < opacityValues.Length; i++)
            {
                double diff = Math.Abs(op - opacityValues[i]);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    closest = i;
                }
            }
            ((ToolStripMenuItem)opacityMenu.DropDownItems[closest]).Checked = true;
        }
        
        private void UpdateSizeCheck()
        {
            foreach (ToolStripMenuItem item in sizeMenu.DropDownItems)
                item.Checked = false;
            
            float[] scales = { 1.8f, 1.7f, 1.6f, 1.5f, 1.4f, 1.3f, 1.2f, 1.1f, 1.0f, 0.9f, 0.8f, 0.7f, 0.6f };
            
            int closest = 0;
            float minDiff = Math.Abs(scale - scales[0]);
            for (int i = 1; i < scales.Length; i++)
            {
                float diff = Math.Abs(scale - scales[i]);
                if (diff < minDiff)
                {
                    minDiff = diff;
                    closest = i;
                }
            }
            ((ToolStripMenuItem)sizeMenu.DropDownItems[closest]).Checked = true;
        }
        
        private void UpdateTopMostCheck()
        {
            foreach (ToolStripMenuItem item in topMostMenu.DropDownItems)
                item.Checked = false;
            if (alwaysOnTop) ((ToolStripMenuItem)topMostMenu.DropDownItems[0]).Checked = true;
            else ((ToolStripMenuItem)topMostMenu.DropDownItems[1]).Checked = true;
        }
        
        public void UpdateStealthCheck()
        {
            bool zh = (AudioData.CurrentLanguage == "zh");
            stealthMenuItem.Checked = stealthMode;
            stealthMenuItem.Text = zh ? 
                (stealthMode ? "关闭神隐模式" : "开启神隐模式") : 
                "Stealth Mode";
        }
        
        public void UpdateMeter()
        {
            switch (mode)
            {
                case MeterMode.Left:
                    currentValue = AudioData.LeftRMS;
                    currentWake = AudioData.LeftWake;
                    break;
                case MeterMode.Right:
                    currentValue = AudioData.RightRMS;
                    currentWake = AudioData.RightWake;
                    break;
                case MeterMode.Master:
                    currentValue = AudioData.MasterRMS;
                    currentWake = AudioData.MasterWake;
                    break;
                case MeterMode.Microphone:
                    currentValue = AudioData.MicRMS;
                    currentWake = AudioData.MicWake;
                    break;
            }
        }
        
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            
            int bgWidth = (int)(backgroundImages[currentSkin].Width * scale);
            int bgHeight = (int)(backgroundImages[currentSkin].Height * scale);
            
            // 1. 绘制背景（溢出1像素消除紫边）
            g.DrawImage(backgroundImages[currentSkin], -1, -1, bgWidth + 2, bgHeight + 2);
            
            // 2. 绘制指针
            float value = Math.Max(0, Math.Min(100, displayValue));
            float angle = startAngle + (endAngle - startAngle) * (value / 100f);
            
            float cx = needleCenterX * scale;
            float cy = needleCenterY * scale;
            
            g.TranslateTransform(cx, cy);
            g.RotateTransform(angle);
            g.TranslateTransform(-needleImages[currentSkin].Width / 2f, -needleImages[currentSkin].Height * scale + 5 * scale);
            g.DrawImage(needleImages[currentSkin], 0, 0, 
                needleImages[currentSkin].Width, 
                needleImages[currentSkin].Height * scale);
            g.ResetTransform();
            
            // 3. 绘制 up 图
            int upWidth = (int)(upImages[currentSkin].Width * scale);
            int upHeight = (int)(upImages[currentSkin].Height * scale);
            int upX = (bgWidth - upWidth) / 2;
            int upY = bgHeight - upHeight;
            g.DrawImage(upImages[currentSkin], upX, upY, upWidth, upHeight);
            
            // 4. 系统音量条（缩短6像素）
            float vol = Math.Max(0, Math.Min(100, displaySystemVolume));
            
            int barY = (int)(bgHeight * 0.72f + 30 * scale + 10 * scale);
            int barHeight = (int)(3 * scale);
            int barLeft = (int)(bgWidth * 0.15f) + (int)(3 * scale);
            int barRight = (int)(bgWidth * 0.85f) - (int)(3 * scale);
            int barWidth = barRight - barLeft;
            
            int fillWidth = (int)(barWidth * vol / 100f);
            g.FillRectangle(grayBrush, barLeft, barY, fillWidth, barHeight);
            
            // 5. 音量数字（字号缩小15%，位置下移3像素）
            // v3.6.4：字体缓存——仅 scale 变化时重建，画刷全局共享
            if (volFontCache == null || volFontCacheScale != scale)
            {
                if (volFontCache != null) volFontCache.Dispose();
                volFontCache = new Font("Arial", ((10 * scale * 2 / 3) + 2.5f) * 0.85f, FontStyle.Bold);
                volFontCacheScale = scale;
            }
            string volText = Math.Round(vol).ToString();
            SizeF textSize = g.MeasureString(volText, volFontCache);
            float textX = barRight - textSize.Width;
            float textY = barY - textSize.Height - 2 * scale - 5 * scale + 3 * scale;
            g.DrawString(volText, volFontCache, volBrush, textX, textY);
            
            // 6. 模式标识图片（以窗口左下角为基准对齐）
            int modeIdx = 0;
            if (mode == MeterMode.Left) modeIdx = 0;
            else if (mode == MeterMode.Right) modeIdx = 1;
            else if (mode == MeterMode.Master) modeIdx = 2;
            else modeIdx = 3;
            
            if (modeImages[modeIdx] != null)
            {
                int imgW = (int)(modeImages[modeIdx].Width * scale);
                int imgH = (int)(modeImages[modeIdx].Height * scale);
                int imgX, imgY;
                // v3.6.1：整体左移6px、上移5px+3px（累计上移8px）
                if (modeIdx == 0 || modeIdx == 1)
                {
                    // Ⓛ和Ⓡ
                    imgX = (int)(5 * scale - 6 * scale - 3 * scale - 6 * scale);
                    imgY = bgHeight - imgH - (int)(5 * scale) + (int)(8 * scale) + (int)(3 * scale) - (int)(5 * scale) - (int)(3 * scale);
                }
                else
                {
                    // Ⓜ和Mic
                    imgX = (int)(5 * scale - 6 * scale - 6 * scale);
                    imgY = bgHeight - imgH - (int)(5 * scale) + (int)(8 * scale) - (int)(5 * scale) - (int)(3 * scale);
                }
                // v3.6.9：Mic 处于「等待接入」时，Mic 标识按 30% 不透明度绘制（变暗），
                // 用来直观区分「Mic 模式在等设备」与「Mic 模式正在采集」；
                // 只影响本张标识图的绘制，不动皮肤位图本身
                if (mode == MeterMode.Microphone && AudioData.MicState == AudioData.MicStatus.Waiting)
                {
                    if (modeDimAttrs == null)
                    {
                        System.Drawing.Imaging.ColorMatrix cm = new System.Drawing.Imaging.ColorMatrix();
                        cm.Matrix33 = 0.30f;
                        modeDimAttrs = new System.Drawing.Imaging.ImageAttributes();
                        modeDimAttrs.SetColorMatrix(cm);
                    }
                    g.DrawImage(modeImages[modeIdx], new Rectangle(imgX, imgY, imgW, imgH),
                        0, 0, modeImages[modeIdx].Width, modeImages[modeIdx].Height,
                        GraphicsUnit.Pixel, modeDimAttrs);
                }
                else
                {
                    g.DrawImage(modeImages[modeIdx], imgX, imgY, imgW, imgH);
                }
            }
            else
            {
                //  fallback 文字
                string label = mode == MeterMode.Left ? "L" : 
                              mode == MeterMode.Right ? "R" : 
                              mode == MeterMode.Master ? "Mix" : "Mic";
                if (modeFontCache == null || modeFontCacheScale != scale)
                {
                    if (modeFontCache != null) modeFontCache.Dispose();
                    modeFontCache = new Font("Arial", 12 * scale, FontStyle.Bold);
                    modeFontCacheScale = scale;
                }
                g.DrawString(label, modeFontCache, redBrush, barLeft - 5 * scale, barY - 30 * scale);
            }
        }
        
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            if (e.Button == MouseButtons.Left)
            {
                dragging = true;
                // v3.6：记录光标与窗口左上角的屏幕偏移，用于自实现拖拽
                Point screen = PointToScreen(e.Location);
                dragOffset = new Point(screen.X - this.Left, screen.Y - this.Top);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (dragging && e.Button == MouseButtons.Left)
            {
                // v3.6：自实现拖拽，替代 SendMessage(SC_MOVE)，以便在拖拽过程中做吸附
                Point cursor = Control.MousePosition;
                int newLeft = cursor.X - dragOffset.X;
                int newTop = cursor.Y - dragOffset.Y;

                // 与其他已注册窗口做吸附检测
                Rectangle self = new Rectangle(newLeft, newTop, this.Width, this.Height);
                foreach (var other in AudioData.GetAllWindows())
                {
                    if (other == null || other.IsDisposed || other == this) continue;
                    // 跳过隐藏/神隐不可见窗口，不参与吸附
                    if (other.Opacity <= 0.05) continue;

                    Rectangle r = new Rectangle(other.Left, other.Top, other.Width, other.Height);

                    // 重合度门槛：边框重合须达到较小窗口边长的 2/3 才允许该方向吸附，
                    // 避免对角临界位置左右/上下吸附反复切换、窗口围绕固定表小范围跳跃
                    const double SnapOverlapRatio = 2.0 / 3.0;

                    // 纵向投影重合长度（左右吸附要求纵向边框重合达标）
                    int vOverlapLen = Math.Min(self.Bottom, r.Bottom) - Math.Max(self.Top, r.Top);
                    double vOverlapRatio = self.Height > 0 && r.Height > 0
                        ? vOverlapLen / (double)Math.Min(self.Height, r.Height) : 0.0;
                    bool vAligned = vOverlapRatio >= SnapOverlapRatio;

                    // 横向投影重合长度（上下吸附要求横向边框重合达标）
                    int hOverlapLen = Math.Min(self.Right, r.Right) - Math.Max(self.Left, r.Left);
                    double hOverlapRatio = self.Width > 0 && r.Width > 0
                        ? hOverlapLen / (double)Math.Min(self.Width, r.Width) : 0.0;
                    bool hAligned = hOverlapRatio >= SnapOverlapRatio;

                    // 计算四个方向的吸附距离
                    int distLeftToRight = Math.Abs(r.Right - self.Left);   // 本左 vs 他右
                    int distRightToLeft = Math.Abs(self.Right - r.Left);   // 本右 vs 他左
                    int distTopToBottom = Math.Abs(r.Bottom - self.Top);   // 本顶 vs 他底
                    int distBottomToTop = Math.Abs(self.Bottom - r.Top);  // 本底 vs 他顶

                    bool snappedHorizontally = false;
                    bool snappedVertically = false;

                    // 横向吸附（左右贴边）：左右距离 ≤16 且纵向边框重合 ≥ 2/3
                    if (vAligned)
                    {
                        if (distLeftToRight <= SnapDistance && distLeftToRight <= distRightToLeft)
                        {
                            newLeft = r.Right;
                            snappedHorizontally = true;
                        }
                        else if (distRightToLeft <= SnapDistance)
                        {
                            newLeft = r.Left - self.Width;
                            snappedHorizontally = true;
                        }
                    }

                    // 纵向吸附（上下贴边）：上下距离 ≤16 且横向边框重合 ≥ 2/3
                    if (hAligned)
                    {
                        if (distTopToBottom <= SnapDistance && distTopToBottom <= distBottomToTop)
                        {
                            newTop = r.Bottom;
                            snappedVertically = true;
                        }
                        else if (distBottomToTop <= SnapDistance)
                        {
                            newTop = r.Top - self.Height;
                            snappedVertically = true;
                        }
                    }

                    // v3.6.1新增：横向吸附触发时，自动对齐上下边缘（优先顶边对齐）
                    if (snappedHorizontally && !snappedVertically)
                    {
                        int topDiff = Math.Abs(self.Top - r.Top);
                        int bottomDiff = Math.Abs(self.Bottom - r.Bottom);
                        if (topDiff <= bottomDiff)
                            newTop = r.Top;  // 顶边对齐
                        else
                            newTop = r.Bottom - self.Height;  // 底边对齐
                    }

                    // v3.6.1新增：纵向吸附触发时，自动对齐左右边缘（优先左边对齐）
                    if (snappedVertically && !snappedHorizontally)
                    {
                        int leftDiff = Math.Abs(self.Left - r.Left);
                        int rightDiff = Math.Abs(self.Right - r.Right);
                        if (leftDiff <= rightDiff)
                            newLeft = r.Left;  // 左边对齐
                        else
                            newLeft = r.Right - self.Width;  // 右边对齐
                    }

                    self = new Rectangle(newLeft, newTop, this.Width, this.Height);
                }

                this.Location = new Point(newLeft, newTop);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
            RememberScreen(); // v3.6.7：拖动结束刷新所在屏记忆（多显示器归位判档用）
        }
        
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            
            float delta = e.Delta > 0 ? 0.02f : -0.02f;
            
            // 根据当前显示模式调整对应通道的音量
            if (mode == MeterMode.Left)
                AudioData.AdjustChannelVolume(0, delta);
            else if (mode == MeterMode.Right)
                AudioData.AdjustChannelVolume(1, delta);
            else if (mode == MeterMode.Microphone)
                AudioData.AdjustMicVolume(delta);
            else
                AudioData.AdjustSystemVolume(delta);
        }
        
        // v3.6.4：停止并释放窗口级 Timer（渲染 + 保存），
        // 防止关闭后继续空转以及 saveTimer 把刚删除的配置写回复活
        private void StopUITimers()
        {
            try { if (renderTimer != null) { renderTimer.Stop(); renderTimer.Dispose(); renderTimer = null; } } catch { }
            try { if (saveTimer != null) { saveTimer.Stop(); saveTimer.Dispose(); saveTimer = null; } } catch { }
        }
        
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);

            StopUITimers();

            // v3.6.4：托盘"暂时退出（保留全部设置）"——既不删除也不写回配置
            if (AudioData.KeepSettingsOnClose)
            {
                return;
            }

            if (e.CloseReason == CloseReason.UserClosing)
            {
                AudioData.DeleteWindowSettings(windowId);
            }
            else
            {
                // v3.6.2：彻底退出时不再把配置写回注册表
                if (!AudioData.SuppressSaveOnClose)
                    SaveCurrentWindow();
            }
        }

        // v3.6：修复 Bitmap GDI 资源泄漏——窗口关闭时显式释放窗口级资源。
        // v3.6.4：素材位图已改为全窗口静态共享，不再在此释放（生命周期与进程一致）
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    StopUITimers();
                    if (volFontCache != null) { volFontCache.Dispose(); volFontCache = null; }
                    if (modeFontCache != null) { modeFontCache.Dispose(); modeFontCache = null; }
                }
                catch { }
            }
            base.Dispose(disposing);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            AudioData.UnregisterWindow(this);
        }
    }
    
    static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private const int SW_RESTORE = 9;

        // v3.6：单实例——第二个进程只负责把已有窗口拉到前台，然后退出
        private const string MutexName = "Global\\VUWidgets_SingleInstance_Mutex_v369";

        // v3.6.4：按"窗口所属进程"匹配已有实例——原版靠窗口标题前缀 "VU Widgets" 匹配，
        // 但右键"新建 VU 表"创建的窗口从未设置 Text（空标题），导致激活失败
        private static IntPtr FindExistingWindow()
        {
            uint currentPid = (uint)Process.GetCurrentProcess().Id;
            List<KeyValuePair<IntPtr, uint>> candidates = new List<KeyValuePair<IntPtr, uint>>();
            
            EnumWindows((hWnd, lParam) =>
            {
                // v3.6.5：不再按可见性过滤——神隐模式下窗口可能不可见，
                // 若只枚举可见窗口，第二个实例将找不到主窗口而无法激活
                uint pid;
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid != 0 && pid != currentPid)
                {
                    candidates.Add(new KeyValuePair<IntPtr, uint>(hWnd, pid));
                }
                return true;
            }, IntPtr.Zero);
            
            foreach (var c in candidates)
            {
                try
                {
                    using (Process p = Process.GetProcessById((int)c.Value))
                    {
                        if (p != null &&
                            p.ProcessName.StartsWith("VU Widgets", StringComparison.OrdinalIgnoreCase))
                        {
                            return c.Key;
                        }
                    }
                }
                catch { }
            }
            
            return IntPtr.Zero;
        }

        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (System.Threading.Mutex mutex = new System.Threading.Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    // 已有实例在运行：找到任意一个 VU Widgets 窗口并激活，然后退出
                    IntPtr found = IntPtr.Zero;
                    try { found = FindExistingWindow(); } catch { }

                    if (found != IntPtr.Zero)
                    {
                        try
                        {
                            ShowWindow(found, SW_RESTORE);
                            SetForegroundWindow(found);
                        }
                        catch { }
                    }
                    return;
                }

                // 启用DPI感知，避免系统缩放导致模糊
                try { SetProcessDPIAware(); } catch { }

                AppDomain.CurrentDomain.AssemblyResolve += (sender, args) => {
                    // v3.6.4：加 try/catch 与完整读取（CopyTo），避免解析失败时在回调里抛异常
                    try
                    {
                        string resourceName = "VolumeMeter." + new AssemblyName(args.Name).Name + ".dll";
                        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                        {
                            if (stream == null) return null;
                            using (MemoryStream ms = new MemoryStream())
                            {
                                stream.CopyTo(ms);
                                return Assembly.Load(ms.ToArray());
                            }
                        }
                    }
                    catch
                    {
                        return null;
                    }
                };

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // v3.6.4：不再每次启动强制写自启动项，改为托盘菜单"开机自启动"由用户控制
                // v3.6.7（L6）：首次运行默认开启自启动（只写一次，此后完全由托盘菜单控制）
                AudioData.EnsureDefaultAutoStart();

                // 自动检测系统语言
                AudioData.AutoDetectLanguage();

                // v3.6.9b：读回 Mic 表内增益（读配置建窗口之前必须先就位，否则首帧电平口径不对）
                AudioData.LoadMicGain();

                // 初始化系统托盘图标
                AudioData.InitTrayIcon();

                // v3.6.6：显示桌面穿透看门狗（Win+D 后把被抬升桌面盖住的表盘重升回来）
                AudioData.EnsureDesktopWatcher();

                List<WindowSettings> allSettings = AudioData.LoadAllWindowSettings();

                if (allSettings.Count > 0)
                {
                    List<VUMeterForm> forms = new List<VUMeterForm>();
                    for (int i = 0; i < allSettings.Count; i++)
                    {
                        VUMeterForm form = new VUMeterForm();
                        if (allSettings.Count == 1)
                            form.Text = "VU Widgets v3.6.9";
                        else
                            form.Text = "VU Widgets v3.6.9 #" + (i + 1);
                        form.ApplySettings(allSettings[i]);
                        forms.Add(form);
                    }

                    foreach (var form in forms)
                    {
                        form.PrepareBeforeShow();
                        form.Show();
                    }

                    Application.Run();
                }
                else
                {
                    VUMeterForm form = new VUMeterForm();
                    form.Text = "VU Widgets v3.6.9";
                    form.PrepareBeforeShow();
                    Application.Run(form);
                }
            }
        }
    }
}
