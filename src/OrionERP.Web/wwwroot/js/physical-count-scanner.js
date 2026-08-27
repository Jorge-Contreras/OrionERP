let scannerControls = null;
let activeVideo = null;
let lastValue = null;
let lastDetectionAt = 0;
let detectionInProgress = false;

export function isSupported() {
  return Boolean(
    navigator.mediaDevices?.getUserMedia
    && window.ZXingBrowser?.BrowserMultiFormatReader);
}

export async function start(videoElement, dotNetReference) {
  await stop(videoElement);

  if (!isSupported()) {
    throw new Error("Barcode scanner is not supported by this browser.");
  }

  activeVideo = videoElement;
  lastValue = null;
  lastDetectionAt = 0;
  detectionInProgress = false;

  const reader = new window.ZXingBrowser.BrowserMultiFormatReader();
  scannerControls = await reader.decodeFromConstraints(
    {
      audio: false,
      video: {
        facingMode: { ideal: "environment" },
        width: { ideal: 1280 },
        height: { ideal: 720 }
      }
    },
    videoElement,
    async (result, error, controls) => {
      if (!result || detectionInProgress) {
        return;
      }

      const value = result.getText?.()?.trim();
      if (!value) {
        return;
      }

      const now = Date.now();
      if (value === lastValue && now - lastDetectionAt < 1400) {
        return;
      }

      lastValue = value;
      lastDetectionAt = now;
      detectionInProgress = true;
      try {
        const accepted = await dotNetReference.invokeMethodAsync("OnBarcodeDetected", value);
        if (accepted) {
          controls.stop();
          stopTracks(videoElement);
          if (navigator.vibrate) {
            navigator.vibrate(80);
          }
        }
      } finally {
        detectionInProgress = false;
      }
    });
}

export async function stop(videoElement) {
  if (scannerControls) {
    try {
      scannerControls.stop();
    } catch {
      // The camera may already have been stopped after a successful match.
    }
    scannerControls = null;
  }

  stopTracks(videoElement || activeVideo);
  activeVideo = null;
  detectionInProgress = false;
}

function stopTracks(videoElement) {
  const stream = videoElement?.srcObject;
  if (stream?.getTracks) {
    stream.getTracks().forEach(track => track.stop());
  }
  if (videoElement) {
    videoElement.srcObject = null;
  }
}
