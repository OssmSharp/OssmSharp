- do not apply the greyscale to the inline notification

guestui:
- remove the "control enabled" inline notification.
- vertically align "Choose settings, then Send." message with the "auto send" text.
- the current marker next to the speed slider does not align exactly to the sent marker after they press send.
- reset speed to 0 when control is lost for whatever reason.
- do not stop the device if the guest window loses focus - allow it to continue in the background.
- remove the "Guest controls" header.

adminui:
- when admin presses stop, stop the last sent preview to match.
- rename "Stop requested" to "Stopping ..." and change it to the warning[yellow] style
