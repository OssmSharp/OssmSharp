# OSSM web controls

Control the OSSM-RS simulator from a browser. **Local only: no physical device
or remote access.** Use admin and guest tabs on the same computer.

## Start

Requires .NET 10, Rust/Cargo, and the `ossm-rs` checkout beside this repository
at `..\ossm-rs`.

Run from this repository:

```powershell
dnx Aspire.Cli@13.5.3 -- run --project src\AppHost\AppHost.csproj
```

Keep the terminal open. Aspire builds and starts the application.

## Use it

1. Open the **Dashboard** URL printed in the terminal. Use the complete link,
   including its login token.
2. In the dashboard's **Resources** page, wait for **admin** and **guest** to
   become healthy. On the **admin** resource, click **Private operator
   (credential link)**. This opens the control page.
3. Set **Speed cap (%)** to `20` and **Maximum depth (%)** to `60`,
   then click **Apply limits**.
4. Click **Create invite**, then **Copy**. Open that link
   in a second browser tab. Keep the admin tab open.
5. The guest joins automatically. Return to admin and click **Allow control**.
6. In the guest tab, set the three vertical sliders and click **Send**.
   Enable **Auto send** to send slider changes automatically.

Purple is selected, amber is sent, and green is the verified current setting.
The live display shows actual simulator position and velocity.
On the operator page, expand **Live feedback** to see it. **Live log** shows
recent activity and follows new entries; scroll up to read earlier entries.

Settings keep running until **Stop**. Switching tabs, losing focus or losing
the connection also stops control and turns Auto send off. Admin must
**Allow control** again before the guest can send more.
Do not share the dashboard or private operator links.

## Stop or reset

- **Stop** requests a stop without ending the session.
- **End session** disconnects the guest. Generate a new link to start another.
- If control is **Locked**, wait for **Unlock** to become enabled,
  click it in admin, then arm separately when ready.
- Press **Ctrl+C** in the terminal to shut down the application and simulator.
