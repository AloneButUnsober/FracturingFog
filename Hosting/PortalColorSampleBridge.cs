// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Hosting/PortalColorSampleBridge.cs
//
// S-X11 (2026-07-28, issue #123) — Linux Wayland IColorSampleBridge. The
// Wayland analogue of X11ColorSampleBridge: where X11 does a raw XGrabPointer
// + XGetImage root read (forbidden under Wayland by design), this asks the
// compositor to run the pick via xdg-desktop-portal.
//
// Mechanism: org.freedesktop.portal.Screenshot.PickColor over the session
// D-Bus. PickColor(parent_window, options) returns a Request object path; the
// compositor then runs ITS OWN eyedropper UI and, once the user picks, emits a
// Response signal on org.freedesktop.portal.Request at that path carrying the
// colour as (ddd) doubles in 0..1. No pixel access, no pointer grab — so it
// works on GNOME/Mutter + KDE/KWin Wayland (and under Xorg where a portal
// backend exists). Right-click / dismiss in the compositor picker comes back as
// Response code != 0 → onCancelled.
//
// Transport: Tmds.DBus.Protocol (managed low-level D-Bus). We predict the
// Request path from a handle_token and subscribe to its Response signal BEFORE
// calling PickColor, per the portal Request lifecycle, so a fast compositor
// can't emit the signal before we're listening.
//
// Limitations:
//   * wlroots/sway (xdg-desktop-portal-wlr) historically ships no PickColor —
//     the call faults; we surface that once via ExternalSampleUnavailable (the
//     same one-shot notice X11ColorSampleBridge raises) and cancel gracefully.
//   * Single colour only — the compositor picker has no live loupe/zoom, unlike
//     the old in-FF crosshair.

using System;
using System.Threading;
using System.Threading.Tasks;

using FracturingFog.Hosting;

using Tmds.DBus.Protocol;

namespace FracturingFog.Hosting;

internal sealed class PortalColorSampleBridge : IColorSampleBridge
{
    private const string PortalService   = "org.freedesktop.portal.Desktop";
    private const string PortalPath      = "/org/freedesktop/portal/desktop";
    private const string ScreenshotIface = "org.freedesktop.portal.Screenshot";
    private const string RequestIface    = "org.freedesktop.portal.Request";

    // Cap the wait on the compositor picker. Generous — the user has to aim and
    // click the compositor's own eyedropper — but bounded so a wedged portal
    // can't pin the bridge active forever.
    private static readonly TimeSpan PickTimeout = TimeSpan.FromSeconds(120);

    private volatile bool _active;
    private readonly object _lock = new();

    // One-shot per process, mirroring X11ColorSampleBridge: raised the first
    // time a pick fails for a reason OTHER than the user cancelling — here, the
    // portal / PickColor being unavailable (wlroots/sway, absent backend, no
    // session bus). Lets the host tell the user once that desktop sampling won't
    // work this session. Interlocked guards the one-shot across the async path.
    public static event Action? ExternalSampleUnavailable;
    private static int s_externalFailureNotified;

    public bool IsActive => _active;

    public void Begin(Action<(byte R, byte G, byte B)> onPicked, Action onCancelled)
    {
        ArgumentNullException.ThrowIfNull(onPicked);
        ArgumentNullException.ThrowIfNull(onCancelled);

        lock (_lock)
        {
            if (_active) { onCancelled(); return; }
            _active = true;
        }

        // Fire-and-forget the async pick; RunAsync owns clearing _active and
        // invoking exactly one of the callbacks.
        _ = RunAsync(onPicked, onCancelled);
    }

    private async Task RunAsync(Action<(byte R, byte G, byte B)> onPicked, Action onCancelled)
    {
        bool picked = false;
        bool hardFailure = false;   // portal unavailable, not a user cancel
        Console.Error.WriteLine("[PortalColorSampleBridge] PickColor requested.");
        Console.Error.Flush();
        try
        {
            var result = await PickColorAsync().ConfigureAwait(false);
            if (result.Ok)
            {
                Console.Error.WriteLine($"[PortalColorSampleBridge] Picked RGB=({result.R},{result.G},{result.B}).");
                Console.Error.Flush();
                picked = true;
                try { onPicked((result.R, result.G, result.B)); } catch { }
            }
            else
            {
                Console.Error.WriteLine("[PortalColorSampleBridge] Portal returned no colour (user cancelled or dismissed).");
                Console.Error.Flush();
            }
        }
        catch (Exception ex)
        {
            // A thrown pick means the portal path itself is unavailable (no bus,
            // no Screenshot portal, no PickColor on this compositor) — distinct
            // from a user cancel, so it drives the one-shot notice.
            hardFailure = true;
            Console.Error.WriteLine($"[PortalColorSampleBridge] PickColor failed: {ex.GetType().Name}: {ex.Message}");
            Console.Error.Flush();
        }
        finally
        {
            _active = false;
            if (!picked)
            {
                try { onCancelled(); } catch { }

                if (hardFailure
                    && Interlocked.Exchange(ref s_externalFailureNotified, 1) == 0)
                {
                    try { ExternalSampleUnavailable?.Invoke(); } catch { }
                }
            }
        }
    }

    private readonly record struct PickResult(bool Ok, byte R, byte G, byte B);

    private static async Task<PickResult> PickColorAsync()
    {
        string? address = Address.Session
            ?? throw new InvalidOperationException("No session D-Bus address (DBUS_SESSION_BUS_ADDRESS unset).");

        using var connection = new Connection(address);
        await connection.ConnectAsync().ConfigureAwait(false);

        // Predict the Request object path so we can subscribe before calling.
        // Portal builds it as /org/freedesktop/portal/desktop/request/<SENDER>/<TOKEN>
        // where SENDER is our unique bus name with the leading ':' dropped and
        // '.' → '_'. handle_token in the call options must match <TOKEN>.
        string unique = connection.UniqueName
            ?? throw new InvalidOperationException("D-Bus connection has no unique name after connect.");
        string sender = unique.TrimStart(':').Replace('.', '_');
        string token = "ff" + Guid.NewGuid().ToString("N");
        string requestPath = $"/org/freedesktop/portal/desktop/request/{sender}/{token}";

        var tcs = new TaskCompletionSource<PickResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        var rule = new MatchRule
        {
            Type      = MessageType.Signal,
            Sender    = PortalService,
            Path      = requestPath,
            Interface = RequestIface,
            Member    = "Response",
        };

        await connection.AddMatchAsync(
            rule,
            static (Message message, object? state) => ReadResponse(message),
            (Exception? ex, PickResult res, object? readerState, object? handlerState) =>
            {
                var completion = (TaskCompletionSource<PickResult>)handlerState!;
                if (ex is not null) completion.TrySetException(ex);
                else completion.TrySetResult(res);
            },
            ObserverFlags.None,
            readerState: null,
            handlerState: tcs).ConfigureAwait(false);

        // PickColor(s parent_window, a{sv} options) → o request_handle
        string handle = await connection.CallMethodAsync(
            CreatePickColorMessage(connection, token),
            static (Message message, object? state) => message.GetBodyReader().ReadObjectPath().ToString())
            .ConfigureAwait(false);

        Console.Error.WriteLine($"[PortalColorSampleBridge] PickColor accepted, request={handle} (predicted {requestPath}).");
        Console.Error.Flush();

        return await tcs.Task.WaitAsync(PickTimeout).ConfigureAwait(false);
    }

    private static MessageBuffer CreatePickColorMessage(Connection connection, string token)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: PortalService,
            path: PortalPath,
            @interface: ScreenshotIface,
            member: "PickColor",
            signature: "sa{sv}");

        writer.WriteString(string.Empty);   // parent_window — no XID/handle

        // options a{sv} = { "handle_token": <s token> }
        ArrayStart options = writer.WriteArrayStart(DBusType.Struct);
        writer.WriteStructureStart();
        writer.WriteString("handle_token");
        writer.WriteSignature("s");         // variant signature
        writer.WriteString(token);          // variant value
        writer.WriteArrayEnd(options);

        return writer.CreateMessage();
    }

    // Response(u response, a{sv} results). response: 0 success, 1 user cancelled,
    // 2 ended other. On success, results carries "color" → (ddd) in 0..1.
    private static PickResult ReadResponse(Message message)
    {
        var reader = message.GetBodyReader();
        uint response = reader.ReadUInt32();
        if (response != 0)
            return new PickResult(false, 0, 0, 0);

        ArrayEnd results = reader.ReadArrayStart(DBusType.Struct);
        while (reader.HasNext(results))
        {
            reader.AlignStruct();
            string key = reader.ReadString();
            if (key == "color")
            {
                reader.ReadSignature();      // variant sig "(ddd)"
                reader.AlignStruct();        // struct aligns to 8
                double r = reader.ReadDouble();
                double g = reader.ReadDouble();
                double b = reader.ReadDouble();
                return new PickResult(true, ToByte(r), ToByte(g), ToByte(b));
            }

            // Unknown key — consume its variant so the loop stays aligned.
            reader.ReadVariantValue();
        }

        return new PickResult(false, 0, 0, 0);
    }

    private static byte ToByte(double c)
        => (byte)Math.Clamp((int)Math.Round(c * 255.0), 0, 255);
}
