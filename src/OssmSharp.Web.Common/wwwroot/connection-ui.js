export function initialize(onConnectionDown) {
  let lost = false;
  const observer = new MutationObserver((mutations) => {
    if (mutations.some(({ target }) => target.id === "blazor-error-ui" && target.style.display === "block")) connectionDown();
  });
  observer.observe(document.body, { subtree: true, attributes: true, attributeFilter: ["style"] });
  function connectionDown() {
    if (lost) return;
    lost = true;
    observer.disconnect();
    document.documentElement.dataset.connectionLost = "true";
    for (const rail of document.querySelectorAll(".preview-rail")) rail.pauseAnimations();
    onConnectionDown?.();
  }
  document.addEventListener(
    "components-reconnect-state-changed",
    (event) => {
      document.getElementById("app-boot-status").hidden = true;
      if (event.detail.state !== "hide") connectionDown();
    },
    { capture: true },
  );
  return {
    circuit: {
      reconnectionOptions: { maxRetries: 0 },
    },
    ready() {
      document.getElementById("app-boot-status").hidden = true;
    },
    failed() {
      document.getElementById("app-boot-status").hidden = true;
      connectionDown();
      const reconnect = document.getElementById("components-reconnect-modal");
      reconnect.classList.remove("components-reconnect-hide");
      reconnect.classList.add("components-reconnect-rejected");
    },
  };
}
