using Baton.Host;

// Headless Baton for development: the same host, sources and openers as the desktop app, driven
// from the console. Commands:
//   devices | pair | log | timings | quit
//   send <deviceId> [activityId]        streamsend <deviceId> <activityId>
//   pull <source> [activityId]          pullstream <source> <activityId>
//   stream <deviceId> <window title>    file <deviceId> <path>
//   apps [filter]                       options <deviceId> <package> [name]  (phone app -> this PC)
//   cmd <ownerDeviceId> <activityId> <play|pause|toggle|seek ms|skip ms|next|previous|volume 0..1>
var storage = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "BatonDevHost");
await using var runtime = new BatonRuntime(storage);
var host = runtime.Host;
var coordinator = runtime.Coordinator;
coordinator.HandoffUpdated += handoff => Console.WriteLine($"[handoff] {handoff.Title}: {handoff.Status?.ToString() ?? "…"} {handoff.Detail}");
host.DeviceConnectionChanged += (id, connected) => Console.WriteLine($"[device] {id} {(connected ? "connected" : "disconnected")}");
await runtime.StartAsync();
Console.WriteLine($"Baton dev host {host.Identity.PcName} ({host.Identity.HostId}) storing in {storage}");

var printed = 0L;
while (Console.ReadLine() is { } line)
{
    var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
    switch (parts.FirstOrDefault())
    {
        case "pair":
            Console.WriteLine(host.OpenPairing());
            break;
        case "devices":
            foreach (var device in coordinator.GetDevices())
            {
                Console.WriteLine($"{device.DeviceId} {device.Name} {device.Kind} online={device.Online} {device.Presence}");
                foreach (var activity in device.Activities)
                {
                    Console.WriteLine($"   {activity.Id} vol={activity.Volume} {activity.Kind} '{activity.Title}' [{activity.App.Name}] {activity.Url} {activity.Content?.Provider}:{activity.Content?.Id} pos={activity.Playback?.PositionAt(DateTimeOffset.UtcNow)} playing={activity.Playback?.Playing}");
                }
            }

            break;
        case "send" when parts.Length > 1:
            await coordinator.SendAsync(parts[1], parts.Length > 2 ? parts[2] : null);
            break;
        case "streamsend" when parts.Length > 2:
            await coordinator.SendAsync(parts[1], parts[2], Baton.Protocol.HandoffModes.Stream);
            break;
        case "pull" when parts.Length > 1:
            await coordinator.PullAsync(parts[1], parts.Length > 2 ? parts[2] : null);
            break;
        case "pullstream" when parts.Length > 2:
            await coordinator.PullAsync(parts[1], parts[2], mode: Baton.Protocol.HandoffModes.Stream);
            break;
        case "cmd" when parts.Length > 2:
        {
            var rest = parts[2].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var action = rest.Length > 1 ? rest[1] : "toggle";
            var value = rest.Length > 2 ? rest[2] : null;
            var command = new Baton.Protocol.MediaCommandPayload(parts[1], rest[0], action,
                action is "seek" or "skip" && long.TryParse(value, out var ms) ? ms : null,
                action == "volume" && double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var volume) ? volume : null);
            Console.WriteLine($"[cmd] {await coordinator.CommandAsync(command)}");
            break;
        }

        case "movetest" when parts.Length > 2:
            Baton.Media.InputInjector.MoveToScreen(int.Parse(parts[1]), int.Parse(parts[2]));
            break;
        case "apps":
            foreach (var app in runtime.Apps.Pc.Apps.Where(app => parts.Length < 2 || app.Name.Contains(parts[1], StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine($"   {app.Name} = {app.Id}");
            }

            Console.WriteLine($"[apps] {runtime.Apps.Pc.Apps.Count} apps");
            break;
        case "options" when parts.Length > 2:
        {
            var rest = parts[2].Split(' ', 2);
            var probe = new Baton.Protocol.Activity($"app:{rest[0]}", parts[1], Baton.Protocol.ActivityKind.WindowStream,
                rest.Length > 1 ? rest[1] : rest[0], new Baton.Protocol.ActivityApp(rest.Length > 1 ? rest[1] : rest[0], rest[0]), DateTimeOffset.UtcNow);
            foreach (var option in coordinator.Options(probe, parts[1], coordinator.LocalDeviceId))
            {
                Console.WriteLine($"   {option.Kind} '{option.Label}' {option.AppId}{option.Url}");
            }

            Console.WriteLine("[options] done");
            break;
        }
        case "phoneapps" when parts.Length > 1:
        {
            var apps = runtime.Apps.AppsOf(Baton.Protocol.Platforms.Android, parts[1]);
            foreach (var app in apps.Where(app => parts.Length < 3 || app.Name.Contains(parts[2], StringComparison.OrdinalIgnoreCase) || app.Id.Contains(parts[2], StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine($"   {app.Name} = {app.Id}");
            }

            Console.WriteLine($"[phoneapps] {apps.Count}");
            break;
        }
        case "choices" when parts.Length > 1:
        {
            // This PC's activities and how each could continue on a phone.
            foreach (var activity in coordinator.LocalActivities)
            {
                var options = coordinator.Options(activity, coordinator.LocalDeviceId, parts[1]);
                Console.WriteLine($"   {activity.Id} '{activity.Title}': " + string.Join(" | ", options.Select((option, index) => $"{index}:{option.Kind} {option.Label}")));
            }

            Console.WriteLine("[choices] done");
            break;
        }
        case "sendwith" when parts.Length > 2:
        {
            var rest = parts[2].Split(' ');
            var activity = coordinator.LocalActivities.First(item => item.Id == rest[0]);
            var option = coordinator.Options(activity, coordinator.LocalDeviceId, parts[1])[int.Parse(rest[1])];
            await coordinator.SendAsync(parts[1], activity.Id, choice: option);
            break;
        }
        case "timings":
            foreach (var (requestId, startedAt, stages) in host.Timeline.Recent())
            {
                Console.WriteLine($"{startedAt.ToLocalTime():HH:mm:ss} {requestId[..8]}: " + string.Join(" | ", stages.Select(stage => $"+{stage.Ms} {stage.Stage}")));
            }

            break;
        case "stream" when parts.Length > 2:
            if (Baton.Media.DesktopWindows.FindByTitle(parts[2]) is { } window)
            {
                await coordinator.SendActivityAsync(parts[1], Baton.Host.Media.WindowSource.ToActivity(window, DateTimeOffset.UtcNow), Baton.Protocol.HandoffModes.Stream);
            }
            else
            {
                Console.WriteLine("No such window.");
            }

            break;
        case "file" when parts.Length > 2:
        {
            // file <deviceId> <path>: hand a local file to a phone from 30 s in.
            var shared = host.Files.Share(parts[2]);
            var activity = new Baton.Protocol.Activity($"file:{shared.FileId}", string.Empty, Baton.Protocol.ActivityKind.LocalMedia,
                Path.GetFileNameWithoutExtension(parts[2]), new Baton.Protocol.ActivityApp("Files", "files"), DateTimeOffset.UtcNow,
                Subtitle: shared.Name, Content: new Baton.Protocol.ActivityContent("file"), File: shared,
                Playback: new Baton.Protocol.Playback(30_000, 0, false, 1, DateTimeOffset.UtcNow));
            await coordinator.SendActivityAsync(parts[1], activity);
            break;
        }

        case "capturetest" when parts.Length > 1:
        {
            // Capture and encode a window for three seconds without any phone involved.
            var target = Baton.Media.DesktopWindows.FindByTitle(string.Join(' ', parts.Skip(1)));
            if (target is null)
            {
                Console.WriteLine("No such window.");
                break;
            }

            using var gpu = Baton.Media.Gpu.Create();
            var (_, _, w, h) = Baton.Media.DesktopWindows.Bounds(target.Handle);
            var (ow, oh) = Baton.Host.Streaming.WindowStreamService.OutputSize(w, h);
            using var converter = new Baton.Media.Nv12Converter(gpu, ow, oh);
            using var encoder = Baton.Media.H264Encoder.Create(gpu, ow, oh);
            long frames = 0, encoded = 0, bytes = 0, failures = 0;
            var output = new MemoryStream();
            encoder.FrameEncoded += frame => { encoded++; bytes += frame.Data.Length; lock (output) { output.Write(frame.Data); } };
            using var capture = new Baton.Media.WindowCapture(gpu, target.Handle);
            capture.FrameFailed += ex => { if (failures++ == 0) Console.WriteLine($"frame failed: {ex}"); };
            capture.FrameArrived += (texture, sw, sh) =>
            {
                frames++;
                encoder.TryEncode(converter.Convert(texture, sw, sh), frames * 16_666);
            };
            capture.Start();
            await Task.Delay(3000);
            var file = Path.Combine(storage, "capturetest.h264");
            lock (output) { File.WriteAllBytes(file, output.ToArray()); }
            Console.WriteLine($"capturetest {target.Title}: {w}x{h} -> {ow}x{oh} via {encoder.Name}; frames {frames}, encoded {encoded}, {bytes / 1024} KiB, failures {failures} -> {file}");
            break;
        }

        case "log":
            foreach (var entry in host.Diagnostics.Snapshot().Where(entry => entry.Sequence >= printed))
            {
                Console.WriteLine($"{entry.Timestamp:HH:mm:ss.fff} {entry.Category} {entry.Event} {entry.DeviceId} {entry.Detail}");
                printed = entry.Sequence + 1;
            }

            break;
        case "quit":
            return;
    }
}
