## Remote Guest Access

GuestWeb supports a same-machine HTTPS reverse proxy, including Tailscale and
Cloudflare Tunnel. Expose **only GuestWeb**, not AdminWeb or the control API.
The friend still needs an invitation, and the local user must allow control.

Add these AppHost parameters in
[appsettings.Development.json](src/OssmSharp.AppHost/appsettings.Development.json):

```json
{
	"Parameters": {
		"guest-public-origin": "https://guest.example",
		"guest-port": 5001
	}
}
```

Restart AppHost, then configure the tunnel to forward HTTPS and WebSockets to
`http://127.0.0.1:5001`. Keep the browser's public `Host` and `Origin`, and forward
`X-Forwarded-Proto: https`. Do not rewrite the Host to the loopback target.
Share the invitation link from the local AdminWeb page.

[Tailscale Serve](https://tailscale.com/kb/1242/tailscale-serve) requires the friend
to be on your tailnet; Funnel provides public access. For Cloudflare, route the
public hostname to the loopback target and set
[`httpHostHeader`](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/configure-tunnels/origin-parameters/)
to the public authority if the tunnel rewrites Host.

The public origin must be HTTPS with no path, credentials, query, or fragment.
Root hosting is supported, not a subpath. Restart when the public URL changes.
Without `guest-public-origin`, access remains local-only. The guest port is
optional, but a fixed port keeps the tunnel target stable.

For standalone hosts, configure:

| Host | Settings |
| --- | --- |
| GuestWeb | `OSSM__Guest__GuestPort=5001`, `OSSM__Guest__Hostname=https://guest.example`, `OSSM__ControlClient__ControlOrigin=http://127.0.0.1:5003` |
| AdminWeb | `OSSM__Admin__AdminPort=5002`, `OSSM__Admin__GuestHostname=https://guest.example`, the same control origin, and `OSSM__ControlClient__ServiceKey` |
| API | `ASPNETCORE_URLS=http://127.0.0.1:5003` and `OSSM__Control__AdminWebServiceKey` matching AdminWeb's key |

Use a random canonical Base64url-encoded 32-byte administrator key. AppHost
generates it automatically. Keep standalone API and AdminWeb listeners private.
GuestWeb always listens on IPv4 loopback; listener overrides are rejected.

AdminWeb rejects invitation URLs with paths, credentials, queries, or fragments.
HTTPS is required except for loopback HTTP URLs.

GuestWeb's server-side API connection authenticates with `Authorization: Bearer
<invitation>`, not a shared guest service key. Authentication does not consume the
invitation; Join consumes it atomically and binds that connection. Rotation blocks
replay without terminating the admitted connection. Control remains separately
approved and is revoked on Leave, End guest, or disconnect.

Remove obsolete `OSSM:Control:GuestWebServiceKey` and GuestWeb's
`OSSM:ControlClient:ServiceKey` from old configurations. There is no legacy guest
key fallback. The API destination stays fixed in server configuration; invitations
and browser headers never select it. Guest identity proves invitation possession,
not a named personal account.

## Web Speed Control

The guest's simple-stroke speed slider is stroke-relative. The browser sends
`floor(selected speed * stroke / 100)` as the firmware velocity percentage.
For example, 50% selected speed with a 10% stroke sends 5%. Depth positions the
stroke; it is not an additional speed multiplier.

The firmware accepts integer percentages. Converted speeds below 1% send zero
and do not produce motion. The runtime, CLI and control API retain firmware
velocity-percent semantics. The guest preview uses the converted selection;
the user preview uses acknowledged transmissions. Both previews are illustrative,
not measurements of physical position or calibrated cycle time.

This follows the firmware's [stroke timing calculation](https://github.com/KinkyMakers/OSSM-hardware/blob/b7f01bf6df1be6f3ebf17dc0e31ed64ddf4c15b7/Software/lib/StrokeEngine/src/StrokeEngine.cpp#L830).
[OSSM-BLE-Web](https://github.com/ReadieFur/OSSM-BLE-Web/blob/main/src/OssmBleClient.ts)
passes speed percentages through unchanged; it does not provide stroke-relative conversion.

## Troubleshooting

**Error:**

```
/usr/local/share/dotnet/packs/Microsoft.macOS.Sdk.net10.0_26.0/26.0.11017/targets/Xamarin.Shared.Sdk.targets(2382,3): error This version of .NET for macOS (26.0.11017) requires Xcode 26.0. The current version of Xcode is 27.0. Either install Xcode 26.0, or use a different version of .NET for macOS. See https://aka.ms/xcode-requirement for more information.
```

Run:

```bash
dotnet workload install macos --version 10.0.401
```
