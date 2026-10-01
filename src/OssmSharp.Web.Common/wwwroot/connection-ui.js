export function initialize(onConnectionDown) {
  const boot = document.getElementById("app-boot-status");
  const reconnect = document.getElementById("components-reconnect-modal");
  let lost = false;
  reconnect.addEventListener("components-reconnect-state-changed", (event) => {
    boot.hidden = true;
    if (event.detail.state !== "hide" && !lost) {
      lost = true;
      onConnectionDown?.();
    }
  });
  return {
    circuit: {
      reconnectionOptions: { maxRetries: 0 },
    },
    ready() {
      boot.hidden = true;
    },
    failed() {
      boot.hidden = true;
      reconnect.classList.remove("components-reconnect-hide");
      reconnect.classList.add("components-reconnect-rejected");
    },
  };
}
