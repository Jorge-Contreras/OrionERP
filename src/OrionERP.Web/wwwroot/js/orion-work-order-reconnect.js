(() => {
  const isWorkOrderRoute = () => /^\/ordenes-trabajo(?:\/\d+)?\/?$/i.test(window.location.pathname);
  const reconnectStates = [
    "components-reconnect-rejected",
    "components-reconnect-failed",
    "components-reconnect-retrying",
    "components-reconnect-paused",
    "components-reconnect-show",
  ];

  const observeReconnectState = () => {
    const modal = document.getElementById("components-reconnect-modal");
    if (!modal) {
      return false;
    }

    let connectionWasLost = false;
    let lastState = "hidden";
    let previouslyFocusedElement = null;

    const getReconnectState = () => reconnectStates.find(state => modal.classList.contains(state)) ?? "hidden";

    const focusReconnectMessage = (state) => {
      window.requestAnimationFrame(() => {
        if (getReconnectState() !== state) {
          return;
        }

        const focusTarget = state === "components-reconnect-failed" || state === "components-reconnect-rejected"
          ? modal.querySelector(`.${state.replace("components-reconnect-", "connection-status__")} .connection-status__primary-action`)
          : modal;
        focusTarget?.focus({ preventScroll: true });
      });
    };

    const setApplicationBlocked = (state) => {
      const isBlocked = state !== "hidden";
      const appShell = document.getElementById("orion-app-shell");

      if (isBlocked) {
        if (lastState === "hidden") {
          previouslyFocusedElement = document.activeElement;
        }
        appShell?.setAttribute("inert", "");
        modal.setAttribute("aria-hidden", "false");
        focusReconnectMessage(state);
      } else {
        appShell?.removeAttribute("inert");
        modal.setAttribute("aria-hidden", "true");
        if (previouslyFocusedElement instanceof HTMLElement && previouslyFocusedElement.isConnected) {
          previouslyFocusedElement.focus({ preventScroll: true });
        }
        previouslyFocusedElement = null;
      }
    };

    const synchronizeAfterReconnect = () => {
      const state = getReconnectState();
      if (state !== lastState) {
        setApplicationBlocked(state);
        lastState = state;
      }

      if (state !== "hidden") {
        connectionWasLost = true;
        return;
      }

      if (connectionWasLost
        && isWorkOrderRoute()
        && modal.dataset.orionSynchronizing !== "true") {
        modal.dataset.orionSynchronizing = "true";
        window.location.reload();
      }
    };

    modal.addEventListener("keydown", event => {
      if (getReconnectState() === "hidden") {
        return;
      }

      if (event.key === "Escape") {
        event.preventDefault();
        return;
      }

      if (event.key === "Tab") {
        const state = getReconnectState();
        const action = state === "components-reconnect-failed" || state === "components-reconnect-rejected"
          ? modal.querySelector(`.${state.replace("components-reconnect-", "connection-status__")} .connection-status__primary-action`)
          : null;
        event.preventDefault();
        (action ?? modal).focus({ preventScroll: true });
      }
    });

    new MutationObserver(synchronizeAfterReconnect).observe(modal, {
      attributes: true,
      attributeFilter: ["class"]
    });
    synchronizeAfterReconnect();
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
