let currentStream = null;
let lastCapture = null;
let lastPreviewUrl = null;

export function isSupported() {
  return !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia);
}

export async function listCameras() {
  if (!navigator.mediaDevices?.enumerateDevices) {
    return [];
  }

  const devices = await navigator.mediaDevices.enumerateDevices();
  return devices
    .filter(device => device.kind === "videoinput")
    .map((device, index) => ({
      deviceId: device.deviceId,
      label: device.label || `Camara ${index + 1}`
    }));
}

export async function start(videoElement, preferredDeviceId) {
  if (!isSupported()) {
    throw new Error("Este navegador no permite acceso directo a la camara.");
  }

  await stop();

  const constraints = buildConstraints(preferredDeviceId);
  let lastError = null;

  for (const constraint of constraints) {
    try {
      currentStream = await navigator.mediaDevices.getUserMedia(constraint);
      videoElement.srcObject = currentStream;
      videoElement.setAttribute("playsinline", "playsinline");
      videoElement.muted = true;
      await videoElement.play();
      return getStreamInfo(currentStream);
    } catch (error) {
      lastError = error;
    }
  }

  throw lastError || new Error("No se pudo abrir la camara.");
}

export async function capture(videoElement, imageMaxPixels, thumbnailMaxPixels) {
  if (!videoElement?.videoWidth || !videoElement?.videoHeight) {
    throw new Error("La camara aun no esta lista para capturar.");
  }

  const image = await renderFrame(videoElement, imageMaxPixels, 0.92);
  const thumbnail = await renderFrame(videoElement, thumbnailMaxPixels, 0.84);
  const info = currentStream ? getStreamInfo(currentStream) : {};
  lastCapture = {
    imageBlob: image.blob,
    thumbnailBlob: thumbnail.blob
  };
  revokePreviewUrl();
  lastPreviewUrl = URL.createObjectURL(image.blob);

  return {
    imageContentType: image.contentType,
    imageWidth: image.width,
    imageHeight: image.height,
    thumbnailContentType: thumbnail.contentType,
    thumbnailWidth: thumbnail.width,
    thumbnailHeight: thumbnail.height,
    deviceId: info.deviceId || "",
    deviceLabel: info.deviceLabel || "",
    facingMode: info.facingMode || ""
  };
}

export function getLastImage() {
  if (!lastCapture?.imageBlob) {
    throw new Error("No hay una foto capturada para guardar.");
  }

  return lastCapture.imageBlob;
}

export function getLastThumbnail() {
  if (!lastCapture?.thumbnailBlob) {
    throw new Error("No hay miniatura capturada para guardar.");
  }

  return lastCapture.thumbnailBlob;
}

export function getPreviewUrl() {
  if (!lastPreviewUrl) {
    throw new Error("No hay una vista previa disponible.");
  }

  return lastPreviewUrl;
}

export function clearLastCapture() {
  revokePreviewUrl();
  lastCapture = null;
}

export async function stop() {
  if (!currentStream) {
    return;
  }

  for (const track of currentStream.getTracks()) {
    track.stop();
  }

  currentStream = null;
}

function buildConstraints(preferredDeviceId) {
  if (preferredDeviceId) {
    return [
      { audio: false, video: { deviceId: { exact: preferredDeviceId } } },
      { audio: false, video: { deviceId: preferredDeviceId } },
      { audio: false, video: true }
    ];
  }

  return [
    { audio: false, video: { facingMode: { exact: "environment" } } },
    { audio: false, video: { facingMode: { ideal: "environment" } } },
    { audio: false, video: true }
  ];
}

function getStreamInfo(stream) {
  const track = stream.getVideoTracks()[0];
  if (!track) {
    return {};
  }

  const settings = track.getSettings ? track.getSettings() : {};
  return {
    deviceId: settings.deviceId || "",
    deviceLabel: track.label || "",
    facingMode: settings.facingMode || ""
  };
}

async function renderFrame(videoElement, maxPixels, quality) {
  const sourceWidth = videoElement.videoWidth;
  const sourceHeight = videoElement.videoHeight;
  const scale = Math.min(1, maxPixels / Math.max(sourceWidth, sourceHeight));
  const width = Math.max(1, Math.round(sourceWidth * scale));
  const height = Math.max(1, Math.round(sourceHeight * scale));
  const canvas = document.createElement("canvas");
  canvas.width = width;
  canvas.height = height;

  const context = canvas.getContext("2d");
  context.drawImage(videoElement, 0, 0, width, height);

  const blob = await canvasToBlob(canvas, quality);
  if (!blob) {
    throw new Error("No se pudo generar la imagen de la camara.");
  }

  return {
    blob,
    contentType: blob.type || "image/jpeg",
    width,
    height
  };
}

async function canvasToBlob(canvas, quality) {
  if (typeof canvas.toBlob === "function") {
    const blob = await new Promise(resolve => {
      let settled = false;
      const timeout = window.setTimeout(() => {
        if (!settled) {
          settled = true;
          resolve(null);
        }
      }, 5000);

      canvas.toBlob(result => {
        if (!settled) {
          settled = true;
          window.clearTimeout(timeout);
          resolve(result);
        }
      }, "image/jpeg", quality);
    });

    if (blob) {
      return blob;
    }
  }

  const dataUrl = canvas.toDataURL("image/jpeg", quality);
  const response = await fetch(dataUrl);
  return await response.blob();
}

window.addEventListener("pagehide", () => {
  if (!currentStream) {
    return;
  }

  for (const track of currentStream.getTracks()) {
    track.stop();
  }

  currentStream = null;
  revokePreviewUrl();
  lastCapture = null;
});

function revokePreviewUrl() {
  if (!lastPreviewUrl) {
    return;
  }

  URL.revokeObjectURL(lastPreviewUrl);
  lastPreviewUrl = null;
}
