(() => {
  const isWorkOrderRoute = () => /^\/ordenes-trabajo(?:\/\d+)?\/?$/i.test(window.location.pathname);

  const observeReconnectState = () => {
    const modal = document.getElementById("components-reconnect-modal");
    if (!modal) {
      return false;
    }

    let connectionWasLost = false;
    const synchronizeAfterReconnect = () => {
      if (modal.classList.contains("components-reconnect-show")
        || modal.classList.contains("components-reconnect-failed")
        || modal.classList.contains("components-reconnect-rejected")) {
        connectionWasLost = true;
        return;
      }

      if (connectionWasLost
        && modal.classList.contains("components-reconnect-hide")
        && isWorkOrderRoute()
        && modal.dataset.orionSynchronizing !== "true") {
        modal.dataset.orionSynchronizing = "true";
        window.location.reload();
      }
    };

    new MutationObserver(synchronizeAfterReconnect).observe(modal, {
      attributes: true,
      attributeFilter: ["class"]
    });
    return true;
  };

  if (!observeReconnectState()) {
    const documentObserver = new MutationObserver(() => {
      if (observeReconnectState()) {
        documentObserver.disconnect();
      }
    });
    documentObserver.observe(document.documentElement, { childList: true, subtree: true });
  }
})();
