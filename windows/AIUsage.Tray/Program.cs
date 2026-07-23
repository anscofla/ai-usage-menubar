namespace AIUsage.Tray;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Local\AIUsageTray", out var createdNew);
        if (!createdNew) return; // another instance in this session — exit quietly

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext());
        GC.KeepAlive(mutex);
    }
}
