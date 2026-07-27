namespace SvrBridge.Tray;

internal static class TraySelfTests
{
    public static void Run()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"svr-bridge-settings-test-{Guid.NewGuid():N}");
        var missingLegacySettings = Path.Combine(testDirectory, "missing-legacy.json");

        try
        {
            var store = new UserSettingsStore(testDirectory, missingLegacySettings);
            var expected = new UserSettings
            {
                StreamerBotAddress = "ws://192.168.1.50:8080/",
                ActionName = "Friendly VR action",
                ActionId = "a0ff6f91-a51e-4b7d-948b-5e03ff4a82f0",
                Password = "self-test-secret",
                GestureMode = SvrBridge.Core.ChordMode.Simultaneous,
                StartBridgeWhenAppOpens = true
            };

            store.Save(expected);
            var settingsJson = File.ReadAllText(
                Path.Combine(testDirectory, "settings.json"));
            Assert(
                !settingsJson.Contains(expected.Password, StringComparison.Ordinal),
                "The password was saved as readable text.");

            var actual = store.Load();
            Assert(actual == expected, "Protected settings did not round-trip.");

            var logDirectory = Path.Combine(testDirectory, "logs");
            var log = new StructuredActivityLog(logDirectory);
            log.Write(
                "self_test",
                "A safe message with password=self-test-secret",
                SvrBridge.Core.BridgeLogLevel.Warning);
            var logText = File.ReadAllText(
                Directory.GetFiles(logDirectory, "*.jsonl").Single());
            Assert(
                !logText.Contains("self-test-secret", StringComparison.Ordinal),
                "The structured log exposed a secret value.");
            Assert(
                logText.Contains("\"event\":\"self_test\"", StringComparison.Ordinal),
                "The structured log omitted its event name.");

            AssertThrows(
                () => UserSettingsStore.Validate(
                    expected with { StreamerBotAddress = "http://not-a-websocket" }),
                "A non-WebSocket address was accepted.");
            AssertThrows(
                () => UserSettingsStore.Validate(
                    expected with { ActionName = "", ActionId = "" }),
                "Empty action settings were accepted.");
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"TRAY SELF-TEST FAIL: {message}");
        }
    }

    private static void AssertThrows(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException($"TRAY SELF-TEST FAIL: {message}");
    }
}
