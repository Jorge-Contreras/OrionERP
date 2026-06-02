(function () {
  const storageKey = "bonhomia.legalFooterAcknowledgement";

  const readAcknowledgement = () => {
    try {
      const raw = window.localStorage.getItem(storageKey);
      return raw ? JSON.parse(raw) : null;
    } catch {
      return null;
    }
  };

  const writeAcknowledgement = (version) => {
    const payload = {
      version,
      acceptedAt: new Date().toISOString()
    };

    window.localStorage.setItem(storageKey, JSON.stringify(payload));
    return payload;
  };

  const updatePanels = (panels, version, acknowledgement) => {
    const isCurrent = Boolean(acknowledgement?.version === version);

    panels.forEach((panel) => {
      panel.hidden = isCurrent;
      panel.classList.toggle("bonhomia-legal-ack--visible", !isCurrent);
    });

    document.documentElement.classList.toggle("bonhomia-legal-ack-visible", !isCurrent);
  };

  const hidePanels = (panels) => {
    panels.forEach((panel) => {
      panel.hidden = true;
      panel.classList.remove("bonhomia-legal-ack--visible");
    });

    document.documentElement.classList.remove("bonhomia-legal-ack-visible");
  };

  window.bonhomiaLegal = {
    initAcknowledgement(version) {
      const panels = Array.from(document.querySelectorAll(`[data-bonhomia-legal-ack="${version}"]`));
      if (panels.length === 0) {
        return;
      }

      const acknowledgement = readAcknowledgement();
      updatePanels(panels, version, acknowledgement);

      panels.forEach((panel) => {
        const button = panel.querySelector("[data-bonhomia-legal-accept]");
        if (!button || button.dataset.bonhomiaLegalBound === "true") {
          return;
        }

        button.dataset.bonhomiaLegalBound = "true";
        button.addEventListener("click", () => {
          try {
            const updated = writeAcknowledgement(version);
            updatePanels(panels, version, updated);
          } catch {
            hidePanels(panels);
          }
        });
      });
    }
  };
})();
