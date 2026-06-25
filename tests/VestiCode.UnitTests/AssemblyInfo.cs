// 部分测试会修改进程级当前工作目录（Directory.SetCurrentDirectory），
// 在 xUnit 默认并行下会相互干扰。关闭并行以保证确定性。
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
