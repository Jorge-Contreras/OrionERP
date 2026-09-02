(() => {
  const signage = document.querySelector("[data-menu-signage]");
  if (!signage) {
    return;
  }

  const DEFAULT_INTERVAL_MS = 8000;
  const positiveOr = (value, fallback) => {
    const parsed = Number(value);
    return Number.isFinite(parsed) && parsed > 0 ? parsed : fallback;
  };

  let intervalMs = positiveOr(signage.dataset.intervalMs, DEFAULT_INTERVAL_MS);
  let rotationTimer = null;

  const slides = () => [...signage.querySelectorAll(".menu-signage__slide")];

  const startRotation = () => {
    if (rotationTimer !== null) {
      window.clearInterval(rotationTimer);
      rotationTimer = null;
    }

    // Una sola imagen es un tablero fijo: no hay nada que rotar. Antes esto
    // también cancelaba el refresco, así que una pantalla de una imagen nunca
    // recogía un reemplazo; ahora el refresco corre aparte.
    const current = slides();
    if (current.length < 2) {
      return;
    }

    let activeIndex = current.findIndex((slide) => slide.classList.contains("is-active"));
    if (activeIndex < 0) {
      activeIndex = 0;
      current[0].classList.add("is-active");
    }

    rotationTimer = window.setInterval(() => {
      const live = slides();
      if (live.length < 2) {
        return;
      }

      live[activeIndex % live.length].classList.remove("is-active");
      activeIndex = (activeIndex + 1) % live.length;
      live[activeIndex].classList.add("is-active");
    }, intervalMs);
  };

  startRotation();

  const manifestUrl = signage.dataset.manifest;
  if (!manifestUrl) {
    // Respaldo estático heredado: no hay manifiesto que consultar.
    return;
  }

  const refreshMs = positiveOr(signage.dataset.refreshMs, 300000);

  const currentSignature = () =>
    slides()
      .map((slide) => `${slide.dataset.imageId}:${slide.dataset.version}`)
      .join("|");

  const preload = (url) =>
    new Promise((resolve) => {
      const image = new Image();
      image.onload = () => resolve(true);
      image.onerror = () => resolve(false);
      image.src = url;
    });

  const applyManifest = async (manifest) => {
    const images = Array.isArray(manifest.images) ? manifest.images : [];
    if (images.length === 0) {
      return;
    }

    const nextSignature = images.map((image) => `${image.id}:${image.v}`).join("|");
    const nextInterval = positiveOr(manifest.intervalMs, intervalMs);
    if (nextSignature === currentSignature() && nextInterval === intervalMs) {
      return;
    }

    // Se precargan todas antes de tocar el DOM para que el cambio no muestre
    // huecos en blanco en la pantalla del local.
    const loaded = await Promise.all(images.map((image) => preload(image.url)));
    if (loaded.some((ok) => !ok)) {
      return;
    }

    const fragment = document.createDocumentFragment();
    images.forEach((image, index) => {
      const element = document.createElement("img");
      element.className = index === 0 ? "menu-signage__slide is-active" : "menu-signage__slide";
      element.src = image.url;
      element.dataset.imageId = String(image.id);
      element.dataset.version = image.v;
      element.alt = image.alt ?? "";
      fragment.appendChild(element);
    });

    signage.replaceChildren(fragment);

    if (Number.isFinite(Number(manifest.transitionMs))) {
      signage.style.setProperty("--menu-signage-transition", `${Number(manifest.transitionMs)}ms`);
    }

    intervalMs = nextInterval;
    startRotation();
  };

  window.setInterval(async () => {
    try {
      const response = await fetch(manifestUrl, { cache: "no-store" });
      if (!response.ok) {
        return;
      }

      await applyManifest(await response.json());
    } catch {
      // Sin red o con la base caída se conserva lo que ya está en pantalla y se
      // reintenta en el siguiente ciclo.
    }
  }, refreshMs);
})();
