namespace CommandTray.Sync;

public static class RelayClientVersion
{
    public const string Header = "X-CommandTray-Client-Version";

    public static string Current { get; } = typeof(RelayClientVersion).Assembly.GetName().Version!.ToString(3);
}
