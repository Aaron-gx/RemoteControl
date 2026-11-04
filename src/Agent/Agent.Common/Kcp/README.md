# Kcp/ — vendored ikcp C# 移植（第三方源码，勿随意改动）

## 来源

- 上游：<https://github.com/Cysharp/KcpTransport>（MIT，见 LICENSE）
- 钉死提交：`e5d8387855edb6257f2033d99fff1b52133b9f1e`（2025-05-13）
- 取的是仓库里的 `src/KcpTransport/LowLevel/`（ikcpc.cs / ikcph.cs / CMethods.cs）——
  原版 [skywind3000/ikcp](https://github.com/skywind3000/ikcp) 的忠实 C# 指针移植，
  与网易 UU远程 逆向所见 dckcp 魔改基线（`uu-reverse/docs/transport/kcp.md`）同源。
- 文件保持上游原样（未改命名空间 KcpTransport.LowLevel），便于日后 diff 升级。

## 为什么 vendor 而不是 NuGet

整个 solution 运行时**零 NuGet 依赖**（仅测试框架），热更包按 DLL 清单打包，
第三方以源码进树、随 Agent.Common.dll 一起发布，不引入包管理链路。

## 用法（见 UdpPunchChannel.cs 的 KcpSession 包装）

UU 的 `DcKcpTransport::Start` 实测参数（uu-reverse 逆向报告）：

```csharp
ikcp_create(conv)                     // conv 从打洞 nonce 前 4 字节派生
ikcp_wndsize(snd=256, rcv=256)
ikcp_nodelay(nodelay=1, interval=5ms, resend=2, nc=1)   // nc=1 关拥塞窗：拥塞交给上层码率阶梯
kcp->rx_minrto = 10                   // ikcph.cs 的字段直接可写
```
