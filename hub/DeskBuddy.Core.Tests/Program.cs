using System.Globalization;
using System.Text;
using DeskBuddy.Core;

internal static partial class Program
{
    private static int _assertions;
    private static readonly List<string> Groups = [];
    private static void Check(bool condition, string message)
    { _assertions++; if (!condition) throw new Exception(message); }
    private static void Throws<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { _assertions++; return; } throw new Exception(message); }
    private static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    { try { await action(); } catch (T) { _assertions++; return; } throw new Exception(message); }
    private static async Task Group(string name, Func<Task> action)
    { await action(); Groups.Add(name); Console.WriteLine("PASS " + name); }
    private static Task Sync(Action action) { action(); return Task.CompletedTask; }
    public static async Task<int> Main(string[] args)
    {
        try
        {
            await Group("224-byte codec, canonical defaults, independent profile arrays", () => Sync(Codec));
            await Group("strict flags, names, reserved bytes, supported actions", () => Sync(InvalidBanks));
            await Group("96 appearance pairs across three home modes preserve wire and JSON IDs", () => Sync(AppearanceRoundtrip));
            await Group("strict HELLO capability counts and independent 4/4/1 defaults", () => Sync(AppearanceCapabilities));
            await Group("bounded byte framing and overflow recovery", () => Sync(Framing));
            await Group("request/reply grammar and legacy typed status", () => Sync(Parsing));
            await Group("strict public JSON, checksum, atomic replace and failed write", () => Sync(Files));
            await Group("local workspace snapshot/draft separation, atomic backup, legacy and corrupt/locked recovery", () => Sync(WorkspaceStoreTests));
            await Group("HELLO/GET/STATUS, preview/save/readback/revert/select", Session);
            await Group("ID matching, interleaved status, timeout, late ACK and cancellation", RequestLifecycle);
            await Group("total request deadline includes blocked transport writes", WriteDeadline);
            await Group("disconnect cancels active/queued work and reconnect never replays", Disconnect);
            await Group("revision conflict, malformed ACK, bad readback and refused output", NegativeReplies);
            await Group("legacy status/explicit control without invented bank capabilities", Legacy);
            await Group("unsupported protocol and malformed capabilities fail closed", Unsupported);
            await Group("old-peer whole-bank appearance guard and capability reread on reconnect", AppearancePeerGuard);
            await Group("animation capability, strict actions and coherent bounded telemetry", () => Sync(AnimationParsing));
            await Group("animation old-peer guard, parameters and capability reread", AnimationPeerGuard);
            await Group("temporary animation ACK/status, expiry, cancellation and no bank writes", AnimationLifecycle);
            await Group("animation errors, malformed readback, deadline and disconnect do not replay", AnimationFailures);
            await Group("BLE capability, management grant/lease grammar and USB-only permission", BleManagementProtocol);
            await Group("GATT 20-byte request fragments and bounded 4096-byte notification lines", BleFraming);
            await Group("GATT cancellation, timeout, disconnect and stale generations fail closed", BleLifecycle);
            await Group("full Core protocol over fake GATT, long SAVE, readback and LOCKED", BleProtocolIntegration);
            await Group("BLE self-revoke ACK and CCCD failure paths never imply unconfirmed revocation", BleDisconnectRevocation);
            await Group("one cached service fallback, strict UUID selection, shared deadline and reference cleanup", GattDiscoveryFallback);
            await Group("serial close deadline retains resource ownership until blocked open/dispose really exits", SerialWorkerCleanup);
            await Group("failed Core connection reports pending serial cleanup without replay or false release", SerialConnectCleanup);
            await Group("poll and late disconnect preserve bilingual BLE reauthorization guidance", () => Sync(ConnectionNoticeRegression));
            await Group("explicit preserve-output and physical-feedback capabilities, strict old-peer defaults", () => Sync(OutputPreservationParsing));
            await Group("old/new ON/OFF configuration and control semantics never issue hidden re-arm", OutputPreservationOperations);
            await Group("animated ON, physical OFF and delayed cancellation use actual readback", OutputPreservationRaces);
            await Group("preservation failure and reconnect clear capabilities without replay", OutputPreservationFailures);
            await Group("R3F strict page capability and optional suppression telemetry", () => Sync(PageOutputParsing));
            await Group("R3F and older page ON/OFF matrices, no rearm and extended-page guard", PageOutputOperations);
            await Group("R3F physical OFF, invalid ACK, disconnect and downgrade do not replay", PageOutputRaces);
            await Group("actual App demo and bilingual paused-output presentation", PageOutputAppDemo);
            await Group("actual App hotkey catalog, exact modifiers and unchanged 224-byte bank", () => Sync(BindingPresetRegression));
            await Group("R3G strict display capabilities, settings grammar, telemetry and sleep-binding codec", () => Sync(DisplayParsing));
            await Group("R3G display independent persistence, physical wake/OFF, polling and unchanged banks", DisplayOperations);
            await Group("R3G old-device guards, failure/cancel/disconnect, serialized display changes and no replay", DisplayFailures);
            await Group("R3H custom timeout contract, minute editor, old capability gates and preserved bank/output", CustomDisplayTimeouts);
            if (args.Length > 0)
            {
                if (args.Length != 2 || args[0] != "--validate-presets") throw new ArgumentException("Expected --validate-presets <directory>.");
                await Group("four delivered preset JSON files load and preserve default mappings", () => Sync(() => PresetFiles(args[1])));
            }
            Console.WriteLine($"RESULT: {Groups.Count} groups, {_assertions} assertions, 0 failures. Host-only; no COM or hardware.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); return 1; }
    }
    private static void Codec()
    {
        var bank = HubDefaults.CreateBank(); var bytes = BankCodec.Encode(bank);
        Check(bytes.Length == 224, "wire length");
        string fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "default-bank.hex")).Trim();
        Check(BankCodec.ToHex(bank) == fixture, "independent canonical fixture");
        Check(BankCodec.ToHex(BankCodec.FromHex(fixture.ToLowerInvariant())) == fixture, "lowercase HEX accepted/canonical output");
        Check(bank.Profiles.Select(p => p.Name).SequenceEqual(new[] { "MEDIA", "STUDY", "WORK" }), "default profile names");
        Check(bank.Profiles.Skip(1).All(p => p.Bindings.SequenceEqual(bank.Profiles[0].Bindings)), "default profile labels do not imply different mappings");
        Check(bank.Profiles[0].Bindings[4] == new HubBinding(BindingKind.Local, 1), "K5 menu default");
        Check(bank.Profiles[0].Bindings[5] == new HubBinding(BindingKind.Local, 2), "K6 local marker");
        Check(bank.Profiles[0].Bindings[6].Value == 0xE9 && bank.Profiles[0].Bindings[7].Value == 0xEA, "encoder directions");
        bank.Profiles[0].Bindings[0] = new(BindingKind.Keyboard, 0x06, 0x03);
        Check(bank.Profiles[1].Bindings[0].Kind == BindingKind.Consumer, "profile arrays must be independent");
        bank.Profiles[0] = bank.Profiles[0] with { Name = "Task 1 ~!", Theme = Theme.Lavender, Face = Face.Robot, Animation = false, EncoderReverse = true, AccentRgb565 = 0xA15F };
        bank = bank with { ActiveProfile = 2 };
        var round = BankCodec.Decode(BankCodec.Encode(bank));
        Check(round.Profiles[0].AccentRgb565 == 0xA15F && round.Profiles[0].EncoderReverse && !round.Profiles[0].Animation && round.ActiveProfile == 2, "full roundtrip");
        Check(Crc32.Compute(Encoding.ASCII.GetBytes("123456789")) == 0xCBF43926, "independent CRC32 check vector");
        foreach (var binding in new[] { new HubBinding(BindingKind.Disabled), new(BindingKind.Keyboard, 4, 255), new(BindingKind.Keyboard, 0x73), new(BindingKind.Local, 4) })
        { bank.Profiles[0].Bindings[1] = binding; Check(BankCodec.Decode(BankCodec.Encode(bank)).Profiles[0].Bindings[1] == binding, "valid binding bounds"); }
    }
    private static void InvalidBanks()
    {
        byte[] good = BankCodec.Encode(HubDefaults.CreateBank());
        foreach (int length in new[] { 0, 1, 223, 225, 448 }) Throws<FormatException>(() => BankCodec.Decode(new byte[length]), "wrong byte count");
        foreach (var (offset, value) in new (int, byte)[] { (0, 0), (0, 2), (1, 3), (2, 2), (3, 1), (7, 1), (8, 8), (9, 12), (9, 255), (10, 2), (11, 2), (14, 3), (15, 1), (16, 0), (16, 0x7F), (16, 0x1F), (16, 0x80), (22, 65), (31, 65) })
        {
            byte[] bad = (byte[])good.Clone(); bad[offset] = value;
            Throws<FormatException>(() => BankCodec.Decode(bad), $"bad bank byte {offset}");
        }
        var fullName = (byte[])good.Clone(); Array.Fill(fullName, (byte)'A', 16, 16);
        Throws<FormatException>(() => BankCodec.Decode(fullName), "name requires terminator");
        foreach (var binding in new[] { new HubBinding((BindingKind)4), new(BindingKind.Disabled, 1), new(BindingKind.Disabled, 0, 1),
            new(BindingKind.Consumer, 0xCC), new(BindingKind.Consumer, 0xCD, 1), new(BindingKind.Keyboard, 3), new(BindingKind.Keyboard, 0x74),
            new(BindingKind.Keyboard, 0x104), new(BindingKind.Local, 0), new(BindingKind.Local, 6), new(BindingKind.Local, 1, 1) })
        {
            var bank = HubDefaults.CreateBank(); bank.Profiles[0].Bindings[0] = binding;
            Throws<FormatException>(() => BankCodec.Encode(bank), "reject unsupported binding");
        }
        foreach (string name in new[] { "", "1234567890123456", "中文", "A\0B", "A\nB" })
        {
            var bank = HubDefaults.CreateBank(); bank.Profiles[0] = bank.Profiles[0] with { Name = name };
            Throws<FormatException>(() => BankCodec.Encode(bank), "reject name");
        }
        Throws<FormatException>(() => BankCodec.Encode(new(0, [])), "wrong profile count");
        foreach (string hex in new[] { "", Convert.ToHexString(good) + "00", " " + Convert.ToHexString(good)[1..], new string('G', 448) })
            Throws<FormatException>(() => BankCodec.FromHex(hex), "invalid HEX");
    }
    private static void AppearanceRoundtrip()
    {
        Check((byte)Theme.Lavender == 3 && (byte)Face.Robot == 3, "original IDs unchanged");
        Check((byte)Theme.Ocean == 4 && (byte)Theme.Blueprint == 7 && (byte)Face.Bunny == 4 && (byte)Face.Wink == 11, "new IDs stable");
        int combinations = 0;
        for (byte mode = 0; mode < BankCodec.HomeModeCount; mode++)
        for (byte theme = 0; theme < BankCodec.ThemeCount; theme++)
            for (byte face = 0; face < BankCodec.FaceCount; face++)
            {
                var bank = HubDefaults.CreateBank();
                int index = (theme + face) % BankCodec.ProfileCount;
                bank.Profiles[index] = bank.Profiles[index] with { Theme = (Theme)theme, Face = (Face)face, Mode = (HomeMode)mode };
                var bytes = BankCodec.Encode(bank);
                Check(bytes[8 + index * 72] == theme && bytes[9 + index * 72] == face && bytes[14 + index * 72] == mode && bytes[15 + index * 72] == 0, "appearance/home mode uses documented wire offsets");
                var restored = BankCodec.FromHex(BankCodec.ToHex(bank));
                Check(restored.Profiles[index].Theme == (Theme)theme && restored.Profiles[index].Face == (Face)face && restored.Profiles[index].Mode == (HomeMode)mode, "96 appearance pairs by three modes roundtrip");
                Check(BankCodec.ToHex(LocalConfigurationStore.ImportJson(LocalConfigurationStore.ExportJson(bank))) == BankCodec.ToHex(bank), "offline JSON does not clamp appearance");
                combinations++;
            }
        Check(combinations == 288, "complete eight-by-twelve-by-three matrix");
        for (int index = 0; index < BankCodec.ProfileCount; index++)
        {
            var bank = HubDefaults.CreateBank();
            bank.Profiles[index] = bank.Profiles[index] with { Theme = (Theme)8 };
            Throws<FormatException>(() => BankCodec.Encode(bank), "theme ID 8 rejected in every profile");
            bank.Profiles[index] = bank.Profiles[index] with { Theme = Theme.Midnight, Face = (Face)12 };
            Throws<FormatException>(() => LocalConfigurationStore.ExportJson(bank), "face ID 12 rejected in every profile");
            bank.Profiles[index] = bank.Profiles[index] with { Face = Face.Pixel, Mode = (HomeMode)3 };
            Throws<FormatException>(() => BankCodec.Encode(bank), "home mode ID 3 rejected in every profile");
        }
        var status = LegacyStatusParser.Parse("STATUS " + FakeDevice.StatusFields(theme: 7, face: 11, mode: 2));
        Check(status.GetUInt32("theme") == 7 && status.GetUInt32("face") == 11 && status.GetUInt32("home_mode") == 2, "new maximum IDs accepted in status");
        string oldFixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "default-bank.hex")).Trim();
        Check(BankCodec.FromHex(oldFixture).Profiles.All(p => p.Mode == HomeMode.Face), "old bank mode zero naturally decodes as Face");
    }
    private const string HelloFields = "proto=1 build=TEST_R3 config_bytes=224 profiles=3 bindings=12 revision=1 preview=0";
    private static HelloInfo ParseCapabilities(string extra = "") => HubProtocol.ParseHello(HubProtocol.ParseReply("HUB 1 OK " + HelloFields + extra));
    private static void AppearanceCapabilities()
    {
        var old = ParseCapabilities();
        Check(old.ThemeCount == 4 && old.FaceCount == 4 && old.HomeModeCount == 1 && old.SupportsTheme(Theme.Lavender) && !old.SupportsTheme(Theme.Ocean), "old HELLO fallback");
        Check(old.SupportsFace(Face.Robot) && !old.SupportsFace(Face.Bunny) && old.SupportsBank(HubDefaults.CreateBank()), "old capability helper");
        var current = ParseCapabilities(" theme_count=8 face_count=12 home_mode_count=3");
        Check(current.ThemeCount == 8 && current.FaceCount == 12 && current.HomeModeCount == 3 && current.SupportsTheme(Theme.Blueprint) && current.SupportsFace(Face.Wink) && current.SupportsHomeMode(HomeMode.VirtualPet), "new HELLO capability");
        Check(!current.SupportsTheme((Theme)8) && !current.SupportsFace((Face)12) && !current.SupportsHomeMode((HomeMode)3), "unknown IDs never supported");
        Check(ParseCapabilities(" theme_count=8").FaceCount == 4 && ParseCapabilities(" face_count=12").ThemeCount == 4, "each omitted count defaults independently");
        Check(ParseCapabilities(" theme_count=5 face_count=9").SupportsFace(Face.Fox), "contiguous partial capabilities accepted");
        Check(ParseCapabilities(" theme_count=8 face_count=12").HomeModeCount == 1 && ParseCapabilities(" home_mode_count=3").ThemeCount == 4, "mode capability defaults independently");
        Check(old.SupportsHomeMode(HomeMode.Face) && !old.SupportsHomeMode(HomeMode.DeskPet) && ParseCapabilities(" home_mode_count=2").SupportsHomeMode(HomeMode.DeskPet), "mode prefix capabilities");
        foreach (string extra in new[] { " theme_count=0", " theme_count=3", " theme_count=9", " theme_count=255", " theme_count=4294967296", " theme_count=-1", " theme_count=+8", " theme_count=8.0", " theme_count=8e0", " theme_count=eight", " theme_count=", " theme_count=8 theme_count=8", " face_count=0", " face_count=3", " face_count=13", " face_count=4294967296", " face_count=-1", " face_count=12.0", " face_count=12 face_count=12", " home_mode_count=0", " home_mode_count=4", " home_mode_count=4294967296", " home_mode_count=-1", " home_mode_count=+3", " home_mode_count=3.0", " home_mode_count=3 home_mode_count=3" })
            Throws<FormatException>(() => ParseCapabilities(extra), "invalid or duplicate capability count");
        var independent = new HelloInfo(1, "old", 224, 3, 12, 1, false);
        Check(independent.ThemeCount == 4 && independent.FaceCount == 4 && independent.HomeModeCount == 1, "source-compatible optional record parameters");
    }
    private static void Framing()
    {
        var framing = new LineFramer(8);
        Check(framing.Feed("ABC"u8).Count == 0, "incomplete line");
        Check(framing.Feed("DEF\r"u8).Count == 0, "CR pending");
        Check(framing.Feed("\n12345678\n"u8).SequenceEqual(new[] { "ABCDEF", "12345678" }), "chunked CRLF and exact boundary");
        Check(framing.Feed("123456789GOOD\nOK\n"u8).SequenceEqual(new[] { "OK" }), "discard overflow tail");
        Check(framing.DroppedLines == 1, "overflow counted");
        Check(framing.Feed(new byte[] { 65, 0x80, 66, 10, 67, 10 }).SequenceEqual(new[] { "C" }), "non ASCII discarded");
        Check(framing.Feed("A\rB\nA\r\r\n\n"u8).Count == 0 && framing.DroppedLines == 4, "embedded CR/blank handling");
        Check(framing.Feed(new byte[100000]).Count == 0, "large invalid chunk remains bounded");
        Check(framing.Feed("\nYES\n"u8).SequenceEqual(new[] { "YES" }), "resync only at newline");
        framing.Feed("OLD"u8); framing.Reset();
        Check(framing.Feed("NEW\n"u8).Single() == "NEW" && framing.DroppedLines == 0, "reset drops partial bytes across sessions");
    }
    private static void Parsing()
    {
        string hex = BankCodec.ToHex(HubDefaults.CreateBank());
        Check(HubProtocol.BuildRequest(int.MaxValue, $"SAVE {uint.MaxValue} {hex}").Length <= 511, "maximum fields fit command limit");
        foreach (string command in new[] { "HELLO", "GET", "STATUS", "REVERT", "SELECT 4294967295 2", "CONTROL OUTPUT 0", "CONTROL LANGUAGE ZH" })
            Check(HubProtocol.BuildRequest(1, command).StartsWith("HUB 1 "), "valid command");
        foreach (string command in new[] { "GET\nREBOOT", " RAWFLASH", "GET ", "CONTROL OUTPUT 2", "SELECT -1 0", "SELECT 0 3", "CONTROL PAGE CALIBRATION", "SAVE 0 AB", new string('A', 600), "CONTROL RUN calc.exe", "HÉLLO" })
            Throws<FormatException>(() => HubProtocol.BuildRequest(1, command), "invalid command");
        Throws<ArgumentOutOfRangeException>(() => HubProtocol.BuildRequest(0, "GET"), "zero request ID");
        Check(HubProtocol.ParseReply("HUB 42 OK revision=1 preview=0").Id == 42, "reply ID");
        Check(HubProtocol.ParseReply("HUB 1 ERR code=STALE").ErrorCode == "STALE", "error code");
        foreach (string line in new[] { "STATUS build=x", "HUB 0 OK", "HUB 2147483648 OK", "HUB 1 MAYBE", "HUB 1 ERR", "HUB 1 ERR code=bad", "HUB 1 OK x=1 x=2", "HUB 1 OK x=", "HUB 1 OK  x=1", "HUB 1 OK\n" })
            Check(!HubProtocol.TryParseReply(line, out _), "reject malformed reply");
        string statusLine = "STATUS " + FakeDevice.StatusFields();
        var status = LegacyStatusParser.Parse(statusLine);
        Check(status.Build == "TEST_R3" && status.Route == "USB" && !status.Armed, "status essentials");
        Check(status.KeysDown!.Length == 6 && status.KeyPresses!.All(n => n == 0), "six-key counters");
        Check(status.Get("unknown") is null && status.GetUInt32("unknown") is null && status.GetBool("unknown") is null, "missing not zero");
        var minimal = LegacyStatusParser.Parse("STATUS build=R2C route=BLE connected=1 armed=0");
        Check(minimal.Connected && minimal.KeysDown is null, "legacy optional telemetry");
        foreach (string invalid in new[] { statusLine.Replace("connected=1", "connected=2"), statusLine.Replace("keys_down=000000", "keys_down=00000x"),
            statusLine.Replace("key_presses=0,0,0,0,0,0", "key_presses=0,0"), statusLine.Replace("battery_est_percent=50", "battery_est_percent=101"),
            statusLine.Replace("route=USB", "route=WIFI"), statusLine + " build=OTHER", statusLine.Replace("enc=0", "enc=2147483648"), statusLine.Replace("face=0", "face=12"), statusLine.Replace("theme=0", "theme=8"), statusLine.Replace("home_mode=0", "home_mode=3") })
            Check(!LegacyStatusParser.TryParse(invalid, out _), "invalid typed status");
    }
    private static void Files()
    {
        var bank = HubDefaults.CreateBank(); string json = LocalConfigurationStore.ExportJson(bank);
        Check(BankCodec.ToHex(LocalConfigurationStore.ImportJson(json)) == BankCodec.ToHex(bank), "JSON roundtrip");
        foreach (string bad in new[] { json.Replace("\"version\": 1", "\"version\": 2"), json.Replace("\"version\": 1", "\"version\": 1, \"version\": 1"),
            json.Replace("\"version\": 1", "\"version\": 1, \"script\": \"calc.exe\""), json.Replace("\"crc32\": \"", "\"crc32\": \"0"),
            "{}", "[]", "{", new string(' ', LocalConfigurationStore.MaxFileBytes + 1), json.Replace("desk-buddy-config", "other") })
            Throws<FormatException>(() => LocalConfigurationStore.ImportJson(bad), "corrupt/unsupported JSON");
        string folder = Path.Combine(Path.GetTempPath(), "desk-core-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "config.json");
        try
        {
            LocalConfigurationStore.SaveAtomic(file, bank); byte[] original = File.ReadAllBytes(file);
            bank = bank with { ActiveProfile = 2 }; LocalConfigurationStore.SaveAtomic(file, bank);
            Check(LocalConfigurationStore.Load(file).ActiveProfile == 2, "atomic replace existing");
            byte[] newer = File.ReadAllBytes(file);
            var badBank = bank with { ActiveProfile = 3 };
            Throws<FormatException>(() => LocalConfigurationStore.SaveAtomic(file, badBank), "invalid bank must not touch destination");
            Check(File.ReadAllBytes(file).SequenceEqual(newer), "validation failure preserved old file");
            Throws<DirectoryNotFoundException>(() => LocalConfigurationStore.SaveAtomic(Path.Combine(folder, "missing", "config.json"), bank), "missing folder error");
            if (OperatingSystem.IsWindows())
            {
                using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                    Throws<IOException>(() => LocalConfigurationStore.SaveAtomic(file, HubDefaults.CreateBank()), "locked target must fail without deletion");
                Check(File.ReadAllBytes(file).SequenceEqual(newer), "write failure preserved prior bytes");
            }
            Check(Directory.GetFiles(folder, ".desk-buddy-*.tmp").Length == 0, "own failed staging files cleaned");
            File.WriteAllBytes(file, new byte[LocalConfigurationStore.MaxFileBytes + 1]);
            Throws<FormatException>(() => LocalConfigurationStore.Load(file), "bounded load");
            Check(original.Length > 448, "local envelope stored");
        }
        finally
        {
            // Only this test's freshly created, flat directory is cleaned; no recursive deletion.
            foreach (string created in Directory.GetFiles(folder)) File.Delete(created);
            Directory.Delete(folder);
        }
    }
    private static async Task Session()
    {
        var device = new FakeDevice(); var clock = new TestClock(); await using var client = new HubClient(device, TimeSpan.FromMilliseconds(100), clock);
        await client.ConnectAsync();
        Check(client.State == HubConnectionState.Ready && client.Capabilities?.Protocol == 1 && client.Configuration?.Revision == 1, "modern connect");
        Check(device.Sent.Count == 3 && device.Sent[0].EndsWith("HELLO") && device.Sent[1].EndsWith("GET") && device.Sent[2].EndsWith("STATUS"), "connect read only order");
        Check(!client.IsStatusStale, "initial status fresh"); clock.Advance(TimeSpan.FromSeconds(4)); Check(client.IsStatusStale, "status ages without traffic");
        clock.RollBackWallClock(TimeSpan.FromDays(1)); Check(client.IsStatusStale, "wall-clock rollback cannot revive stale telemetry");
        await client.RefreshStatusAsync(); Check(!client.IsStatusStale, "new status refreshes age");
        var editor = client.Configuration!.Bank; editor.Profiles[0].Bindings[0] = new(BindingKind.Keyboard, 6, 1);
        Check(client.Configuration!.Bank.Profiles[0].Bindings[0].Kind == BindingKind.Consumer, "cached config isolated from editor");
        var preview = await client.PreviewAsync(editor, 1);
        Check(preview.Preview && preview.Revision == 2 && !client.Status!.Armed && device.PersistedHex != device.Hex, "preview readback, off, not persisted");
        var saved = await client.SaveAsync(editor, 2);
        Check(!saved.Preview && saved.Revision == 3 && device.PersistedHex == device.Hex, "save readback confirmed");
        var selected = await client.SelectProfileAsync(2, 3);
        Check(selected.Preview && selected.Bank.ActiveProfile == 2, "runtime select");
        var reverted = await client.RevertAsync();
        Check(!reverted.Preview && reverted.Bank.ActiveProfile == 0 && reverted.Bank.Profiles[0].Bindings[0].Kind == BindingKind.Keyboard, "revert persisted config");
        await client.ControlAsync("OUTPUT 1"); Check(client.Status!.Armed, "explicit arm only");
        await client.ControlAsync("ROUTE BLE"); Check(client.Status!.Route == "BLE" && !client.Status.Armed, "route disarms");
        await client.ControlAsync("LANGUAGE ZH"); Check(client.Status!.Get("language") == "ZH", "language control");
        await client.ControlAsync("PAGE INFO"); Check(client.Status!.GetUInt32("page") == 4, "page control");
        await client.ControlAsync("COUNTERS RESET");
        var ids = device.Sent.Where(s => s.StartsWith("HUB ")).Select(s => int.Parse(s.Split(' ')[1])).ToArray();
        Check(ids.Distinct().Count() == ids.Length && ids.SequenceEqual(ids.Order()), "unique increasing IDs");
        await client.DisconnectAsync(); Check(client.Status is null && client.Configuration is null && client.IsStatusStale, "disconnect clears UI data");
    }
    private static async Task RequestLifecycle()
    {
        var device = new FakeDevice(); await using var client = new HubClient(device, TimeSpan.FromMilliseconds(45)); await client.ConnectAsync();
        device.Intercept = line =>
        {
            int id = int.Parse(line.Split(' ')[1]);
            device.Emit("STATUS " + FakeDevice.StatusFields());
            device.Emit($"HUB {id + 100} OK revision=999 preview=0 hex={device.Hex}");
            device.DefaultReply(line);
        };
        Check((await client.GetAsync()).Revision == 1, "wrong ID/status interleave cannot ack");
        string? captured = null;
        device.Intercept = line => { captured = line; device.Emit("STATUS " + FakeDevice.StatusFields()); device.Emit("HUB 99999 OK revision=2 preview=0 hex=" + device.Hex); };
        int before = device.Sent.Count;
        await ThrowsAsync<TimeoutException>(async () => await client.GetAsync(), "unmatched response timeout");
        Check(device.Sent.Count == before + 1 && client.Configuration is null && client.IsStatusStale, "timeout clears uncertain data without retry");
        device.DefaultReply(captured!); Check(client.Configuration is null, "late acknowledgement does not restore timed-out config");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(15));
        await ThrowsAsync<OperationCanceledException>(async () => await client.GetAsync(cancel.Token), "request cancellation");
        device.Intercept = null; Check((await client.GetAsync()).Revision == 1, "fresh request after cancellation");
    }
    private static async Task Disconnect()
    {
        var device = new FakeDevice(); await using var client = new HubClient(device, TimeSpan.FromSeconds(1)); await client.ConnectAsync();
        device.Intercept = _ => { };
        int before = device.Sent.Count;
        Task first = client.GetAsync(); Task queued = client.RefreshStatusAsync();
        device.Cut();
        await ThrowsAsync<IOException>(() => first, "disconnect ends active request");
        await ThrowsAsync<IOException>(() => queued, "disconnect ends queued request");
        Check(client.State == HubConnectionState.Disconnected && client.Configuration is null && client.Status is null && device.Sent.Count == before + 1, "no queued replay");
        device.Intercept = null; await client.ConnectAsync();
        Check(device.Sent.Count == before + 4 && device.Sent.Skip(before + 1).All(s => s.EndsWith("HELLO") || s.EndsWith("GET") || s.EndsWith("STATUS")), "reconnect only reads");
        Check(device.Sent.Select(s => int.Parse(s.Split(' ')[1])).Distinct().Count() == device.Sent.Count, "IDs not reused on reconnect");
        device.Intercept = _ => throw new IOException("Cable removed during write");
        await ThrowsAsync<IOException>(async () => await client.RefreshStatusAsync(), "write IOException");
        Check(client.State == HubConnectionState.Disconnected && client.Status is null, "failed write disconnects");
    }
    private static async Task WriteDeadline()
    {
        var device = new FakeDevice(); await using var client = new HubClient(device, TimeSpan.FromMilliseconds(35)); await client.ConnectAsync();
        var blockedWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.DelayWrite = _ => blockedWrite.Task;
        await ThrowsAsync<TimeoutException>(async () => await client.RefreshStatusAsync(), "blocked write has a deadline");
        Check(!device.IsOpen && client.State == HubConnectionState.Disconnected && client.Status is null, "blocked transport closed and status invalidated");
        blockedWrite.SetResult(); device.DelayWrite = null;
        await client.ConnectAsync(); Check(client.State == HubConnectionState.Ready, "explicit reconnect after blocked writer");
    }
    private static async Task NegativeReplies()
    {
        var device = new FakeDevice(); await using var client = new HubClient(device, TimeSpan.FromMilliseconds(100)); await client.ConnectAsync();
        var bank = client.Configuration!.Bank;
        int before = device.Sent.Count;
        await ThrowsAsync<HubProtocolException>(async () => await client.PreviewAsync(bank, 99), "local stale revision");
        Check(device.Sent.Count == before && client.Configuration is null, "stale edit not sent");
        await client.GetAsync(); device.Revision = 2;
        await ThrowsAsync<HubProtocolException>(async () => await client.PreviewAsync(bank, 1), "device stale revision");
        Check(client.Configuration is null, "device STALE invalidates config");
        await client.GetAsync();
        device.Intercept = line =>
        {
            if (line.Contains(" PREVIEW ")) device.Emit($"HUB {line.Split(' ')[1]} OK revision=3 preview=1");
            else device.DefaultReply(line);
        };
        await ThrowsAsync<HubProtocolException>(async () => await client.PreviewAsync(bank, 2), "ACK without matching readback");
        Check(client.Configuration is null, "bad readback cache invalidated");
        device.Intercept = line => device.Emit($"HUB {line.Split(' ')[1]} OK revision=2 revision=2 preview=0 hex={device.Hex}");
        await ThrowsAsync<FormatException>(async () => await client.GetAsync(), "malformed matching ACK fails immediately");
        device.Intercept = null; await client.GetAsync(); device.CanArm = false;
        await ThrowsAsync<HubProtocolException>(async () => await client.ControlAsync("OUTPUT 1"), "arming precondition denial");
        Check(client.Status is null, "no stale armed state after denial");
        device.Intercept = line => device.Emit($"HUB {line.Split(' ')[1]} ERR code=SAVE_FAILED");
        await ThrowsAsync<HubProtocolException>(async () => await client.SaveAsync(bank, 2), "device save failure");
        Check(client.Configuration is null, "save failure does not keep unverified config");
        device.Intercept = null; await client.GetAsync();
        device.Emit("STATUS " + FakeDevice.StatusFields(revision: 55)); Check(client.Configuration is null, "unsolicited revision change invalidates editor baseline");
        await ThrowsAsync<FormatException>(async () => await client.ControlAsync("OUTPUT 1\nREBOOT"), "control injection");
    }
    private static async Task Legacy()
    {
        var device = new FakeDevice { Legacy = true }; await using var client = new HubClient(device, TimeSpan.FromMilliseconds(25)); await client.ConnectAsync();
        Check(client.IsLegacy && client.Configuration is null && client.Capabilities is null, "legacy fallback has no config capability");
        await ThrowsAsync<HubProtocolException>(async () => await client.GetAsync(), "legacy bank editing disabled");
        await client.ControlAsync("OUTPUT 1"); Check(client.Status!.Armed && device.Sent.Contains("HID ON"), "legacy explicit output observed");
        await client.ControlAsync("ROUTE BLE"); Check(client.Status!.Route == "BLE" && !client.Status.Armed, "legacy route observed");
        await client.ControlAsync("LANGUAGE ZH"); await client.ControlAsync("PAGE DIAG"); await client.ControlAsync("COUNTERS RESET");
        device.CanArm = false;
        await ThrowsAsync<HubProtocolException>(async () => await client.ControlAsync("OUTPUT 1"), "legacy rejected arm is not fabricated success");
    }
    private static async Task Unsupported()
    {
        foreach (string capabilities in new[] { "proto=2 build=future config_bytes=224 profiles=3 bindings=12 revision=1 preview=0", "proto=1 build=bad config_bytes=225 profiles=3 bindings=12 revision=1 preview=0", "proto=1 build=bad config_bytes=224 profiles=3 bindings=12 revision=bad preview=0", HelloFields + " theme_count=9 face_count=12", HelloFields + " theme_count=8 face_count=13", HelloFields + " home_mode_count=4" })
        {
            var device = new FakeDevice { Intercept = null };
            device.Intercept = line => device.Emit($"HUB {line.Split(' ')[1]} OK {capabilities}");
            await using var client = new HubClient(device, TimeSpan.FromMilliseconds(50));
            await ThrowsAsync<Exception>(async () => await client.ConnectAsync(), "invalid HELLO must fail");
            Check(client.State == HubConnectionState.Faulted && client.Configuration is null && !device.IsOpen, "fail closed without legacy masquerade");
            Check(device.Sent.Count == 1, "unknown layout must not send GET or mutate");
        }
    }
    private static async Task AppearancePeerGuard()
    {
        var device = new FakeDevice(); await using var client = new HubClient(device, TimeSpan.FromMilliseconds(100)); await client.ConnectAsync();
        Check(!client.IsLegacy && client.Capabilities is { ThemeCount: 4, FaceCount: 4 }, "original protocol-1 peer retains its four/four capability");
        for (int index = 0; index < 3; index++)
            foreach (int style in new[] { 0, 1, 2, 3 })
            {
                var bank = HubDefaults.CreateBank();
                bank.Profiles[index] = bank.Profiles[index] with { Theme = style == 0 ? Theme.Ocean : Theme.Midnight, Face = style == 1 ? Face.Bunny : Face.Pixel, Mode = style == 2 ? HomeMode.DeskPet : style == 3 ? HomeMode.VirtualPet : HomeMode.Face };
                Check(!client.Capabilities!.SupportsBank(bank), "unsupported inactive profile exposed to UI");
                int before = device.Sent.Count;
                try { await client.PreviewAsync(bank, 1); throw new Exception("unsupported preview accepted"); }
                catch (HubProtocolException ex) { Check(ex.Code == "UNSUPPORTED_STYLE", "preview appearance error code"); }
                try { await client.SaveAsync(bank, 1); throw new Exception("unsupported save accepted"); }
                catch (HubProtocolException ex) { Check(ex.Code == "UNSUPPORTED_STYLE", "save appearance error code"); }
                Check(device.Sent.Count == before && client.Configuration?.Revision == 1 && client.Status is not null, "unsupported bank sends nothing and keeps valid device readback");
            }
        var oldSupported = HubDefaults.CreateBank(); oldSupported.Profiles[0] = oldSupported.Profiles[0] with { Theme = Theme.Lavender, Face = Face.Robot };
        await client.SaveAsync(oldSupported, 1);
        Check(client.Configuration?.Bank.Profiles[0].Face == Face.Robot && !client.Configuration.Preview, "old peer still saves original IDs");
        await client.DisconnectAsync(); Check(client.Capabilities is null, "disconnect clears prior capabilities");
        device.ThemeCount = 8; device.FaceCount = 12; device.HomeModeCount = 3; await client.ConnectAsync();
        Check(client.Capabilities is { ThemeCount: 8, FaceCount: 12, HomeModeCount: 3 }, "reconnect reads upgraded capabilities");
        var extended = HubDefaults.CreateBank() with { ActiveProfile = 2 }; extended.Profiles[2] = extended.Profiles[2] with { Theme = Theme.Blueprint, Face = Face.Wink, Mode = HomeMode.VirtualPet };
        Check(client.Capabilities!.SupportsBank(extended), "new peer supports extended offline bank");
        var result = await client.SaveAsync(extended, client.Configuration!.Revision);
        Check(result.Bank.Profiles[2].Theme == Theme.Blueprint && result.Bank.Profiles[2].Face == Face.Wink && result.Bank.Profiles[2].Mode == HomeMode.VirtualPet && client.Status!.GetUInt32("home_mode") == 2, "extended SAVE and fresh status preserve new IDs");
        await client.DisconnectAsync(); device.ThemeCount = null; device.FaceCount = null; device.HomeModeCount = null; device.SetBankForTest(HubDefaults.CreateBank()); await client.ConnectAsync();
        Check(client.Capabilities is { ThemeCount: 4, FaceCount: 4, HomeModeCount: 1 }, "reconnect to older peer forgets expanded capabilities");
        int oldCount = device.Sent.Count;
        await ThrowsAsync<HubProtocolException>(async () => await client.SaveAsync(extended, client.Configuration!.Revision), "downgraded peer blocks extended bank");
        Check(device.Sent.Count == oldCount, "downgraded capability blocks before transmission");
        await client.DisconnectAsync(); device.SetBankForTest(extended);
        await ThrowsAsync<HubProtocolException>(async () => await client.ConnectAsync(), "GET cannot exceed declared capabilities");
        Check(client.State == HubConnectionState.Faulted && client.Configuration is null, "inconsistent peer fails closed");
    }
    private static void PresetFiles(string directory)
    {
        var cases = new Dictionary<string, (byte Theme, byte Face, HomeMode Mode)[]>
        {
            ["01-original-face.json"] = [(0, 0, HomeMode.Face), (0, 0, HomeMode.Face), (0, 0, HomeMode.Face)],
            ["02-soft-companions.json"] = [(4, 2, HomeMode.DeskPet), (6, 4, HomeMode.DeskPet), (7, 3, HomeMode.DeskPet)],
            ["03-interactive-pets.json"] = [(5, 6, HomeMode.VirtualPet), (6, 9, HomeMode.VirtualPet), (4, 7, HomeMode.VirtualPet)],
            ["04-expressive-faces.json"] = [(5, 11, HomeMode.Face), (6, 10, HomeMode.Face), (3, 8, HomeMode.Face)]
        };
        Check(Directory.GetFiles(directory, "*.json").Select(Path.GetFileName).Order().SequenceEqual(cases.Keys.Order()), "exact delivered preset set");
        var defaults = HubDefaults.CreateBank(); var old = ParseCapabilities();
        foreach (var (name, expected) in cases)
        {
            var bank = LocalConfigurationStore.Load(Path.Combine(directory, name));
            Check(bank.ActiveProfile == 0, "preset starts with MEDIA selected");
            for (int i = 0; i < 3; i++)
            {
                var profile = bank.Profiles[i];
                Check(((byte)profile.Theme, (byte)profile.Face, profile.Mode) == expected[i], "preset appearance matches its supplied description");
                Check(profile.Name == defaults.Profiles[i].Name && profile.Bindings.SequenceEqual(defaults.Profiles[i].Bindings), "preset keeps original names and all twelve mappings");
            }
            Check(old.SupportsBank(bank) == name.StartsWith("01-", StringComparison.Ordinal), "only original preset can be sent to old four/four/one peer");
            Console.WriteLine("PRESET " + name + " LOAD PASS; default mappings preserved; old-peer=" + old.SupportsBank(bank));
        }
        Check(BankCodec.ToHex(LocalConfigurationStore.Load(Path.Combine(directory, "01-original-face.json"))) == BankCodec.ToHex(defaults), "original preset is byte-identical to original defaults");
    }
}

internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private long _ticks;
    public override DateTimeOffset GetUtcNow() => _now;
    public override long GetTimestamp() => _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public void Advance(TimeSpan amount) { _now += amount; _ticks += amount.Ticks; }
    public void RollBackWallClock(TimeSpan amount) => _now -= amount;
}

internal sealed class FakeDevice : IManagementTransport
{
    public ManagementChannel Channel { get; init; } = ManagementChannel.Usb;
    public event Action<string>? LineReceived;
    public event Action<Exception?>? Disconnected;
    public List<string> Sent { get; } = [];
    public bool IsOpen { get; private set; }
    public bool Legacy { get; init; }
    public bool CanArm { get; set; } = true;
    public Action<string>? Intercept { get; set; }
    public Func<CancellationToken, Task>? DelayWrite { get; set; }
    public string Hex { get; private set; } = BankCodec.ToHex(HubDefaults.CreateBank());
    public string PersistedHex { get; private set; } = BankCodec.ToHex(HubDefaults.CreateBank());
    public uint Revision { get; set; } = 1;
    public int? ThemeCount { get; set; }
    public int? FaceCount { get; set; }
    public int? HomeModeCount { get; set; }
    public bool? AnimationPreviewCapability { get; set; }
    public bool? BleManagementCapability { get; set; }
    public bool? PreserveOutputCapability { get; set; }
    public bool? PetInputFeedbackCapability { get; set; }
    public bool? PreservePageOutputCapability { get; set; }
    public uint BleManagementState { get; set; }
    public uint BleManagementRemaining { get; set; }
    public bool Calibrating { get; set; }
    private uint _demoAction, _demoRemaining;
    private bool _preview;
    private bool _armed;
    public bool Armed => _armed;
    private string _route = "USB", _language = "EN";
    private int _page;
    public Task OpenAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); IsOpen = true; return Task.CompletedTask; }
    public Task CloseAsync() { IsOpen = false; return Task.CompletedTask; }
    public async Task SendLineAsync(string line, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); if (!IsOpen) throw new IOException("Closed transport");
        Sent.Add(line);
        if (DelayWrite is not null) await DelayWrite(cancellationToken);
        // A transport must never continue an old write after a deadline/connection change.
        cancellationToken.ThrowIfCancellationRequested(); if (!IsOpen) throw new IOException("Closed transport");
        if (Intercept is not null) Intercept(line); else DefaultReply(line);
    }
    public void Emit(string line) => LineReceived?.Invoke(line);
    public void Cut() { IsOpen = false; Disconnected?.Invoke(new IOException("Cable unplugged")); }
    public static string StatusFields(uint revision = 1, bool preview = false, bool armed = false, string route = "USB", string language = "EN", int page = 0, byte profile = 0, byte theme = 0, byte face = 0, byte mode = 0) =>
        $"build=TEST_R3 uptime_ms=123 route={route} connected=1 armed={(armed ? 1 : 0)} encoder=1 touch=1 calibrated=1 stored=1 enc=0 rotation=0 presses=0 cw=0 ccw=0 switch_down=0 sent=0 dropped=0 page={page} keys_down=000000 key_presses=0,0,0,0,0,0 key_releases=0,0,0,0,0,0 markers=0 vbat_est_mV=3900 vbat_raw=1900 vbat_sampled=1 vbat_age_ms=0 usb_vbus=1 language={language} language_saved=1 language_save_error=none battery_est_percent=50 ui_last_us=0 ui_max_us=0 usb_connected=1 ble_connected=0 config_revision={revision} config_preview={(preview ? 1 : 0)} profile_index={profile} theme={theme} face={face} home_mode={mode}";
    private string CurrentStatus()
    {
        var bank = BankCodec.FromHex(Hex); var profile = bank.Profiles[bank.ActiveProfile];
        return StatusFields(Revision, _preview, _armed, _route, _language, _page, bank.ActiveProfile, (byte)profile.Theme, (byte)profile.Face, (byte)profile.Mode) +
            (AnimationPreviewCapability == true ? $" demo_action={_demoAction} demo_remaining_ms={_demoRemaining}" : "") +
            (BleManagementCapability == true ? $" ble_manage={BleManagementState} ble_manage_remaining_ms={BleManagementRemaining} ble_link={(Channel == ManagementChannel.Ble ? 1 : 0)} ble_secured={(Channel == ManagementChannel.Ble ? 1 : 0)} ble_bonded={(Channel == ManagementChannel.Ble ? 1 : 0)} manage_owner=0 manage_lease_ms=0" : "") +
            (PetInputFeedbackCapability == true ? " pet_direction=0 pet_elapsed_ms=0" : "") +
            (PreservePageOutputCapability is not null ? $" hid_suppressed={(PreservePageOutputCapability == true && _page is 2 or 3 or 5 ? 1 : 0)}" : "");
    }
    public void CancelAnimation() { _demoAction = _demoRemaining = 0; }
    public void PhysicalOutputOff() { _armed = false; CancelAnimation(); }
    public void AdvanceAnimation(uint milliseconds)
    { if (milliseconds >= _demoRemaining) CancelAnimation(); else _demoRemaining -= milliseconds; }
    public void SetBankForTest(HubBank bank) { Hex = PersistedHex = BankCodec.ToHex(bank); _preview = false; }
    public void DefaultReply(string line)
    {
        if (!line.StartsWith("HUB "))
        {
            if (line == "STATUS") Emit("STATUS " + CurrentStatus());
            else ApplyControl(line switch { "HID ON" => "OUTPUT 1", "HID OFF" => "OUTPUT 0", "USB" => "ROUTE USB", "BLE" => "ROUTE BLE", "LANG EN" => "LANGUAGE EN", "LANG ZH" => "LANGUAGE ZH", "HOME" => "PAGE HOME", "DIAG" => "PAGE DIAG", "INFO" => "PAGE INFO", "ACTIVITY" => "PAGE ACTIVITY", _ => line });
            return;
        }
        if (Legacy) { Emit("Unknown command. HELP"); return; }
        var parts = line.Split(' '); string id = parts[1], verb = parts[2];
        void Ok(string fields) => Emit($"HUB {id} OK {fields}");
        void Error(string code) => Emit($"HUB {id} ERR code={code}");
        if (verb == "HELLO") { Ok($"proto=1 build=TEST_R3 config_bytes=224 profiles=3 bindings=12 revision={Revision} preview={(_preview ? 1 : 0)}" + (ThemeCount is int themes ? $" theme_count={themes}" : "") + (FaceCount is int faces ? $" face_count={faces}" : "") + (HomeModeCount is int modes ? $" home_mode_count={modes}" : "") + (AnimationPreviewCapability is bool animation ? $" animation_preview={(animation ? 1 : 0)}" : "") + (BleManagementCapability is bool management ? $" ble_management={(management ? 1 : 0)}" : "") + (PreserveOutputCapability is bool preserve ? $" preserve_output={(preserve ? 1 : 0)}" : "") + (PetInputFeedbackCapability is bool feedback ? $" pet_input_feedback={(feedback ? 1 : 0)}" : "") + (PreservePageOutputCapability is bool pages ? $" preserve_page_output={(pages ? 1 : 0)}" : "")); return; }
        if (verb == "GET") { Ok($"revision={Revision} preview={(_preview ? 1 : 0)} hex={Hex}"); return; }
        if (verb == "STATUS") { Ok(CurrentStatus()); return; }
        if (verb == "ANIMATE")
        {
            if (AnimationPreviewCapability != true) { Error("UNSUPPORTED"); return; }
            if (parts[3] == "STOP") CancelAnimation();
            else
            {
                if (Calibrating) { Error("BUSY"); return; }
                var bank = BankCodec.FromHex(Hex);
                if (parts[3] == "WALK" && bank.Profiles[bank.ActiveProfile].Mode == HomeMode.Face) { Error("INCOMPATIBLE"); return; }
                _demoAction = parts[3] switch { "IDLE" => 1, "BLINK" => 2, "HAPPY" => 3, "WALK" => 4, "TAP" => 5, "SLEEP" => 6, "WAKE" => 7, _ => throw new Exception("Unvalidated animation command") };
                _demoRemaining = 6000; if (PreserveOutputCapability != true) _armed = false; _page = 0;
            }
            Ok($"demo_action={_demoAction} demo_remaining_ms={_demoRemaining} armed={(_armed ? 1 : 0)}"); return;
        }
        if (verb == "CONTROL")
        {
            string command = string.Join(' ', parts.Skip(3));
            if (command.StartsWith("MANAGE ", StringComparison.Ordinal))
            {
                if (Channel != ManagementChannel.Usb && command != "MANAGE 0") { Error("FORBIDDEN"); return; }
                if (BleManagementCapability != true) { Error("UNSUPPORTED"); return; }
                if (Channel == ManagementChannel.Usb && PreserveOutputCapability != true) _armed = false;
                BleManagementState = command == "MANAGE 1" ? 1u : 0u; BleManagementRemaining = BleManagementState == 1 ? 60000u : 0u;
                Ok($"ble_manage={BleManagementState} ble_manage_remaining_ms={BleManagementRemaining} armed={(_armed ? 1 : 0)}"); return;
            }
            if (command == "OUTPUT 1" && !CanArm) { Error("NOT_READY"); return; }
            ApplyControl(command);
            Ok(command.Split(' ')[0] switch
            {
                "OUTPUT" => $"armed={(_armed ? 1 : 0)}", "ROUTE" => $"route={_route} armed={(_armed ? 1 : 0)}", "LANGUAGE" => $"language={_language} saved=1" + (PreserveOutputCapability == true ? $" armed={(_armed ? 1 : 0)}" : ""),
                "PAGE" => $"page={_page} armed={(_armed ? 1 : 0)}", "COUNTERS" => "reset=1", _ => ""
            }); return;
        }
        if (verb is "PREVIEW" or "SAVE" or "SELECT")
        {
            if (uint.Parse(parts[3], CultureInfo.InvariantCulture) != Revision) { Error("STALE"); return; }
            if (verb == "SELECT") Hex = BankCodec.ToHex(BankCodec.FromHex(Hex) with { ActiveProfile = byte.Parse(parts[4], CultureInfo.InvariantCulture) });
            else Hex = parts[4];
            if (verb == "SAVE") PersistedHex = Hex;
            _preview = verb != "SAVE";
        }
        else if (verb == "REVERT") { Hex = PersistedHex; _preview = false; }
        else { Error("UNSUPPORTED"); return; }
        CancelAnimation(); Revision++; if (PreserveOutputCapability != true) _armed = false;
        Ok($"revision={Revision} preview={(_preview ? 1 : 0)}" + (verb == "SAVE" ? " saved=1" : "") + (PreserveOutputCapability == true ? $" armed={(_armed ? 1 : 0)}" : ""));
    }
    private void ApplyControl(string command)
    {
        if (command == "OUTPUT 1" || command.StartsWith("ROUTE ", StringComparison.Ordinal) ||
            (command.StartsWith("PAGE ", StringComparison.Ordinal) && command != "PAGE HOME")) CancelAnimation();
        string[] fields = command.Split(' ');
        switch (fields[0])
        {
            case "OUTPUT": _armed = fields[1] == "1" && CanArm; break;
            case "ROUTE": if (PreserveOutputCapability != true || _route != fields[1]) _armed = false; _route = fields[1]; break;
            case "LANGUAGE": _language = fields[1]; break;
            case "PAGE": _page = fields[1] switch { "HOME" => 0, "SETTINGS" => 1, "DIAG" => 2, "INFO" => 4, "ACTIVITY" => 5, "MANAGEMENT" => 6, "LANGUAGE" => 7, "TOOLS" => 8, _ => 0 }; if (_page == 0 ? PreserveOutputCapability != true : PreservePageOutputCapability != true) _armed = false; break;
        }
    }
}
