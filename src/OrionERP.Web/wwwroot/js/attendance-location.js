window.orionAttendance = (() => {
  const captureLocation = () => new Promise((resolve) => {
    const capturedAt = new Date().toISOString();
    if (!navigator.geolocation) {
      resolve({ capturedAt });
      return;
    }
    navigator.geolocation.getCurrentPosition(
      position => resolve({
        latitude: position.coords.latitude,
        longitude: position.coords.longitude,
        accuracyMeters: position.coords.accuracy,
        capturedAt
      }),
      () => resolve({ capturedAt }),
      { enableHighAccuracy: true, timeout: 12000, maximumAge: 0 });
  });

  const postJson = async (url, body) => {
    let response;
    try {
      response = await fetch(url, {
        method: "POST",
        credentials: "same-origin",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
    } catch {
      throw new Error("Sin conexion con OrionERP. No se registro la asistencia; use una correccion al recuperar el servicio.");
    }
    const payload = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(payload.message || "No se pudo completar la operacion.");
    return payload;
  };

  return {
    captureLocation,
    pairKiosk: pairingCode => postJson("/api/workforce/kiosk/pair", { pairingCode }),
    kioskPunch: async (badgeToken, pin, eventType) => postJson("/api/workforce/kiosk/punch", {
      badgeToken,
      pin,
      eventType,
      idempotencyKey: crypto.randomUUID(),
      location: await captureLocation()
    }),
    scanBadgeQr: async () => {
      if (!("BarcodeDetector" in window) || !navigator.mediaDevices?.getUserMedia) {
        throw new Error("Este navegador no admite lectura QR. Captura el código del gafete manualmente.");
      }
      const stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: "environment" }, audio: false });
      const overlay = document.createElement("div");
      overlay.style.cssText = "position:fixed;inset:0;z-index:9999;background:#0b1b24ee;display:grid;place-items:center;padding:1rem";
      const panel = document.createElement("div");
      panel.style.cssText = "width:min(100%,38rem);color:white;text-align:center";
      const video = document.createElement("video");
      video.autoplay = true; video.playsInline = true; video.srcObject = stream;
      video.style.cssText = "width:100%;border-radius:1rem;border:3px solid #39c5c8";
      const hint = document.createElement("p"); hint.textContent = "Coloca el QR del gafete dentro del recuadro";
      const cancel = document.createElement("button"); cancel.textContent = "Cancelar"; cancel.className = "btn btn-light";
      panel.append(video, hint, cancel); overlay.append(panel); document.body.append(overlay);
      const detector = new BarcodeDetector({ formats: ["qr_code"] });
      return await new Promise((resolve, reject) => {
        let stopped = false;
        const close = value => {
          if (stopped) return; stopped = true;
          stream.getTracks().forEach(track => track.stop()); overlay.remove(); resolve(value);
        };
        cancel.onclick = () => close(null);
        const scan = async () => {
          if (stopped) return;
          try {
            const codes = await detector.detect(video);
            if (codes.length > 0) { close(codes[0].rawValue); return; }
            requestAnimationFrame(scan);
          } catch (error) { stream.getTracks().forEach(track => track.stop()); overlay.remove(); reject(error); }
        };
        video.onloadedmetadata = () => requestAnimationFrame(scan);
      });
    }
  };
})();
