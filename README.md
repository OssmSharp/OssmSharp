# OssmSharp

OssmSharp connects to your OSSM device over Bluetooth. Use your browser to connect
the device, stop motion, and give a guest access to the controls.

You need a compatible OSSM device and a computer with Bluetooth.

## Choose how your guest will connect

A guest on another device needs one of these options. Choose and set it up
before sharing an invitation. Skip this step if you are only trying the app
on your own computer.

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

Follow [Guest access from another device](#guest-access-from-another-device)
to connect your choice to OssmSharp. Do not forward the app's ports directly.

## Download OssmSharp

Open [Releases](https://github.com/argium/OssmSharp/releases/latest) and download
the **OssmSharp.Web** file for your computer:

- **Windows:** choose the file ending in `win-x64.zip`. Use 64-bit Windows 10
  (version 2004 or later) or Windows 11.
- **macOS:** choose the file ending in `osx-arm64.tar.gz`. Use an Apple silicon
  Mac running macOS 26 or later.

Extract the whole download into a folder you can write to. Keep its files
together. You do not need to install .NET or development tools.

The separate **OssmSharp.Cli** download is for people who prefer terminal controls.
Most people should use **OssmSharp.Web**.

## Start OssmSharp

Turn on Bluetooth and power on your device. Allow Bluetooth access if your
computer asks.

### Windows

Open the extracted folder, right-click its background, and choose **Open in
Terminal**. Run:

```powershell
.\OssmSharp.Web.exe
```

Windows may warn that the download is unfamiliar. Only allow it if you downloaded
it from the OssmSharp release page and trust it.

### macOS

Open Terminal, type `cd ` (including the space), drag the extracted folder into
Terminal, and press <kbd>Return</kbd>. Then run:

```sh
./OssmSharp.Web.app/Contents/MacOS/OssmSharp.Web
```

These downloads are not notarized by Apple. If macOS blocks the app, open
**System Settings > Privacy & Security** and use **Open Anyway** for OssmSharp,
then try again. Only do this for a download you trust. Do not move the executable
out of its `.app` folder.

### Open the controls

Open [http://127.0.0.1:5002](http://127.0.0.1:5002) in your browser after the app
starts. Keep the terminal open while using the app. Press
<kbd>Ctrl</kbd>+<kbd>C</kbd> in that terminal when you want to close OssmSharp.

To check which version you downloaded, run the same command with `--version`:

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

Click **Stop** to stop motion. Click **End guest** to end the guest's session.
Only share an invitation with the person you want to join.

Each invitation can be used once. **Refresh invitation** creates a new link and
ends the current guest session.

## Guest access from another device

Run your chosen tunnel or HTTPS proxy on the computer running OssmSharp.
Point it at `http://127.0.0.1:5001`. Expose only this guest address, not the
control page or any other app ports. For port forwarding, forward to the HTTPS
proxy, not directly to port 5001.

Replace `https://guest.example` below with your guest's HTTPS address.
Stop OssmSharp first, then restart it with that address.

**Windows PowerShell:**

```powershell
$env:OSSM__Guest__Hostname = 'https://guest.example'
.\OssmSharp.Web.exe
```

**macOS Terminal:**

```sh
OSSM__Guest__Hostname=https://guest.example ./OssmSharp.Web.app/Contents/MacOS/OssmSharp.Web
```

Copy a new invitation from the control page and share it with your guest.
Use an address such as `https://guest.example`, without a page path or extra
text after the hostname.

<details>
<summary>Tunnel settings if the guest page will not open</summary>

The tunnel must support WebSockets, preserve the public `Host` and `Origin`,
and send `X-Forwarded-Proto: https`. For Cloudflare, set
[`httpHostHeader`](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/configure-tunnels/origin-parameters/)
to your public hostname, such as `guest.example`.

</details>

## Use the guest controls

Choose a pattern and settings, then click **Send**. Turn on **Auto send** if you
want later changes to be sent as you make them. Use **Stop** to stop motion.

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
