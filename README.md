# OssmSharp

OssmSharp connects to your OSSM device over Bluetooth. Use your browser to connect
the device, stop motion, and give a guest access to the controls.

You need a compatible OSSM device and a computer with Bluetooth.

## Install the tools

Follow the instructions for your computer. You only need to do this once.

### Windows

Use 64-bit Windows 10 (version 2004 or later) or Windows 11.
Open PowerShell and paste:

```powershell
winget install --exact --id Microsoft.DotNet.SDK.10
winget install --exact --id Microsoft.PowerShell
winget install --exact --id Git.Git
```

Close PowerShell and open it again after installation.

### macOS

Use an Apple silicon Mac running macOS 26 or later.
Install [Homebrew](https://brew.sh), then open Terminal and paste:

```sh
brew install --cask dotnet-sdk
brew install git powershell
```

Install Xcode 27 from the App Store, open it once, and let it finish installing
its components. Then run:

```sh
sudo xcode-select --switch /Applications/Xcode.app/Contents/Developer
sudo dotnet workload install macos
```

Enter your Mac password if prompted, then open a new Terminal window.

## Start OssmSharp

Turn on Bluetooth and power on your device. Allow Bluetooth access if your
computer asks.

Run these commands in PowerShell on Windows or Terminal on macOS:

```powershell
git clone https://github.com/argium/OssmSharp.git
cd OssmSharp
pwsh -File ./run-web.ps1
```

The first start may take a few minutes. Wait until you see **User controls**,
then open [http://127.0.0.1:5002](http://127.0.0.1:5002) in your browser.

Keep the terminal open while using the app. Press <kbd>Ctrl</kbd>+<kbd>C</kbd>
in that terminal when you want to close OssmSharp.

To start it again later, open a terminal in the `OssmSharp` folder and run:

```powershell
pwsh -File ./run-web.ps1
```

## Connect and invite a guest

1. Click **Connect device** and wait for the device to connect.
2. Copy the link under **Invite a guest** and give it to your guest.
3. After the guest opens the link and joins, click **Allow control**.

Click **Stop** to stop motion. Click **End guest** to end the guest's session.
Only share an invitation with the person you want to join.

Guest links work on the computer running OssmSharp by default. A guest on another
device needs the setup below.

## Guest access from another device

Set up an HTTPS tunnel using [Tailscale Funnel](https://tailscale.com/kb/1223/funnel)
or [Cloudflare Tunnel](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/).
Point it at `http://127.0.0.1:5001`. Expose only this guest address, not the
control page or any other ports.

Replace `https://guest.example` below with your tunnel's HTTPS address.
Stop OssmSharp first, then restart it with that address.

**Windows PowerShell:**

```powershell
$env:OSSM__Guest__Hostname = 'https://guest.example'
pwsh -File ./run-web.ps1
```

**macOS Terminal:**

```sh
OSSM__Guest__Hostname=https://guest.example pwsh -File ./run-web.ps1
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

## If something goes wrong

- **Device will not connect:** check that it is powered on, Bluetooth is enabled,
  and OssmSharp has Bluetooth permission.
- **A command is not found:** close and reopen your terminal after installing
  the tools.
- **A port is already in use:** close the other copy of OssmSharp. If another
  app uses those ports, start OssmSharp with:

```powershell
pwsh -File ./run-web.ps1 -GuestPort 5101 -AdminPort 5102 -ApiPort 5103
```

- **Xcode version error on macOS:** update Xcode, open it to finish setup, then
  run:

```sh
sudo dotnet workload update
sudo dotnet workload install macos
```

Logs are saved in the `logs` folder inside your `OssmSharp` folder.
