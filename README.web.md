# OssmSharp web app

Control your OSSM device and share guest access from a browser. You need a
compatible device and a computer with Bluetooth.

## Choose how your guest will connect

For guests on another device, set up one of these options before sharing a link.
Skip this step for local use.

- **[Tailscale](https://tailscale.com/kb/1223/funnel):** use Funnel for a public
  link, or [Serve](https://tailscale.com/kb/1242/tailscale-serve) if your guest
  also uses your Tailscale network.
- **[ngrok](https://ngrok.com/docs/universal-gateway/http):** create an HTTPS
  link to the app without changing your router settings.
- **[Cloudflare Tunnel](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/):**
  use a hostname on your domain without opening router ports.
- **Port forwarding (advanced):** forward your router's HTTPS port to an HTTPS
  proxy on the computer running OssmSharp. You need a valid certificate and
  an internet connection that allows incoming connections.

Then follow [Guest access](#guest-access). Do not expose the app's ports directly.

## Download OssmSharp

Open [Releases](https://github.com/OssmSharp/OssmSharp/releases/latest) and download
the **OssmSharp.Web** file for your computer:

- **Windows:** choose the file ending in `win-x64.zip`. Use 64-bit Windows 10
  (version 2004 or later) or Windows 11.
- **macOS:** choose the file ending in `osx-arm64.tar.gz`. Use an Apple silicon
  Mac running macOS 26 or later.

Extract all files into a writable folder and keep them together. No .NET
installation is needed.

Choose **OssmSharp.Cli** only if you prefer terminal controls.

## Start OssmSharp

Turn on Bluetooth and power on your device. Allow Bluetooth access if your
computer asks.

### Windows

Open the extracted folder, right-click its background, and choose **Open in
Terminal**. Run:

```powershell
.\OssmSharp.Web.exe
```

If Windows warns about the download, allow it only if it came from the release
page and you trust it.

### macOS

Open Terminal, type `cd ` (including the space), drag the extracted folder into
Terminal, and press <kbd>Return</kbd>. Then run:

```sh
./OssmSharp.Web.app/Contents/MacOS/OssmSharp.Web
```

The app is not notarized by Apple. If macOS blocks it, open
**System Settings > Privacy & Security** and use **Open Anyway** for OssmSharp,
then retry. Only allow downloads you trust. Keep the `.app` folder intact.

### Open the controls

Open [http://127.0.0.1:5002](http://127.0.0.1:5002). Keep the terminal open.
Press <kbd>Ctrl</kbd>+<kbd>C</kbd> there to close the app.

To check the version, add `--version`:

```powershell
.\OssmSharp.Web.exe --version
```

```sh
./OssmSharp.Web.app/Contents/MacOS/OssmSharp.Web --version
```

## Connect and invite a guest

1. Click **Connect device** and wait for the device to connect.
2. Copy the link under **Invite a guest** and give it to your guest.
3. After the guest opens the link and joins, click **Allow control**.

Use **Stop** to stop motion and **End guest** to end guest access.
Share invitations only with your intended guest.

Each invitation can be used once. **Refresh invitation** creates a new link and
ends the current guest session.

## Guest access

Run your tunnel or HTTPS proxy on the same computer, pointing at
`http://127.0.0.1:5001`. Expose only this guest address, never the control page
or other app ports. Forward router ports to the HTTPS proxy, not port 5001.

Stop OssmSharp. Replace `https://guest.example` with your public HTTPS address
and restart:

**Windows PowerShell:**

```powershell
$env:OSSM__Guest__Hostname = 'https://guest.example'
.\OssmSharp.Web.exe
```

**macOS Terminal:**

```sh
OSSM__Guest__Hostname=https://guest.example ./OssmSharp.Web.app/Contents/MacOS/OssmSharp.Web
```

Share a new invitation from the control page. The HTTPS address must have no
page path or extra text after the hostname.

<details>
<summary>Tunnel settings if the guest page will not open</summary>

The tunnel must support WebSockets, preserve the public `Host` and `Origin`,
and send `X-Forwarded-Proto: https`. For Cloudflare, set
[`httpHostHeader`](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/configure-tunnels/origin-parameters/)
to your public hostname, such as `guest.example`.

</details>

## Use the guest controls

Choose a pattern and settings, then click **Send**. **Auto send** sends later
changes as you make them. **Stop** stops motion.

Only one guest can be active at a time.

## If something goes wrong

- **A port is already in use:** close the other copy of OssmSharp. If another
  app uses those ports, add these settings to your usual start command:

```text
--OSSM:Web:GuestPort=5101 --OSSM:Web:AdminPort=5102 --OSSM:Web:ApiPort=5103
```

  Open `http://127.0.0.1:5102` for the controls and point your guest tunnel at
  `http://127.0.0.1:5101`.
- **The device does not appear:** check that Bluetooth is on, Bluetooth access
  is allowed for OssmSharp, and another app is not connected to the device.
- **macOS says it cannot open the app:** follow the opening steps above and keep
  the whole `.app` folder intact.
- **A newer download behaves differently:** run `--version` and include that
  value when asking for help.

Logs are saved in the `logs` folder in your terminal's current folder.
