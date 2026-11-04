using Agent.Common;
using Agent.Worker;

namespace Agent.Coordinator;

/// <summary>
/// 在 Coordinator 进程内的一条 **STA 线程**上承载采集引擎（原 Agent.Worker.exe 的全部职责）。
///
/// 为什么必须是"一条带消息循环的 STA 线程"而不是随便找个线程池线程 —— 原 Worker 是独立进程，
/// 它的主线程同时承担三件事，合并后必须原样保留：
///   1. WinForms 消息循环：WorkerContext 是 ApplicationContext，剪贴板（OLE）写入必须在
///      STA 线程上做（ClipboardWatcher.Invoker 依赖这条线程 marshal）；
///   2. 低级键盘钩子（CursorArbiter 的 WH_KEYBOARD_LL）的宿主：钩子装在哪条线程、
///      回调就在哪条线程跑，这条线程必须泵消息且不能被长活儿占住（否则整机键盘发滞）；
///   3. 引擎生命周期：ExitThread() 只结束这条线程的消息循环，Coordinator 的托盘/中继不受影响 ——
///      对应旧架构"Worker 进程退出、Coordinator 看护拉起"的语义。
/// </summary>
internal sealed class WorkerHost
{
    private readonly AgentConfig _cfg;
    private readonly Logger _log;
    private readonly WorkerBridge _bus;
    private readonly object _gate = new();
    private Thread? _thread;
    private volatile WorkerContext? _ctx;   // 看门狗硬重启时用来给引擎线程投 WM_QUIT

    public WorkerHost(AgentConfig cfg, Logger log, WorkerBridge bus)
    {
        _cfg = cfg;
        _log = log;
        _bus = bus;
    }

    public bool IsRunning
    {
        get { lock (_gate) return _thread is { IsAlive: true }; }
    }

    /// <summary>启动引擎线程（已在跑则忽略）。幂等，可被看护反复调用。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_thread is { IsAlive: true }) return;
            var t = new Thread(EngineLoop)
            {
                IsBackground = true,          // 进程退出时不挡路（引擎资源在 finally/Dispose 里收尾）
                Name = "worker-engine",
            };
            t.SetApartmentState(ApartmentState.STA);   // 剪贴板 OLE + 消息循环都要求 STA
            _thread = t;
            t.Start();
        }
    }

    /// <summary>
    /// 请求引擎停止：通过桥发 Stop 消息（WorkerContext 收到后会置停并 ExitThread）。
    /// 用消息而不是硬杀线程，是因为引擎里的采集/编码/钩子需要走正常 Dispose 收尾
    /// （钩子不卸会拖慢整机输入，编码器不关会留下 ffmpeg 进程）。
    /// </summary>
    public void RequestStop()
    {
        try
        {
            _bus.SendToWorker(new ProtocolMessage(MessageType.Stop, PayloadCodec.Empty()));
        }
        catch (Exception ex) { _log.Debug($"发送停止指令异常：{ex.Message}"); }
    }

    /// <summary>
    /// 看门狗兜底（2026-10-09 VSAQ 事故）：泵已卡死/状态断流时调用。
    /// 给引擎 STA 线程投 WM_QUIT 请求退出；引擎正常收尾会触发 MarkWorkerDisconnected →
    /// 既有的看护自动拉起新引擎。若引擎线程连 WM_QUIT 都响应不了（钩子回调里卡死），
    /// 泵侧已把"卡在哪个消息上"记进日志，进程级重启（热更新）是最后手段。
    /// </summary>
    public void DumpEngineStackAndRequestExit()
    {
        try
        {
            var t = _thread;
            if (t is { IsAlive: true })
            {
                _ctx?.RequestHardExit();
            }
            else
            {
                _log.Warn("看门狗：引擎线程已不在（IsAlive=false），跳过硬重启");
            }
        }
        catch (Exception ex) { _log.Warn($"看门狗硬重启失败：{ex.Message}"); }
    }

    private void EngineLoop()
    {
        WorkerContext? ctx = null;
        // 引擎沿用独立的 worker-*.log：排障时"协调器日志"和"采集/编码日志"还是分开的（运维习惯不变）
        var wlog = new Logger("worker", minLevel: _cfg.MinLogLevel, echoConsole: false);
        // 泵诊断日志也进引擎日志（协调器侧 Logger 已订阅一份；引擎重启后由新 wlog 接管）
        void OnPumpLog(string s) => wlog.Warn(s);
        _bus.PumpLog += OnPumpLog;
        try
        {
            ctx = new WorkerContext(_cfg, wlog, SessionInfo.CurrentId, _bus);
            _ctx = ctx;
            _bus.MarkWorkerConnected();
            ctx.Start();
            Application.Run(ctx);        // 消息循环，直到 ExitThread（Stop 指令 / 无显示器 / 引擎自行退出）
        }
        catch (Exception ex)
        {
            wlog.Error("采集引擎线程异常退出", ex);
        }
        finally
        {
            _bus.PumpLog -= OnPumpLog;
            _ctx = null;
            try { ctx?.Dispose(); } catch { }
            _bus.MarkWorkerDisconnected("引擎线程退出");
            wlog.Info("采集引擎线程已结束");
        }
    }
}
