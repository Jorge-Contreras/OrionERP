import React from "react";
import { Composition } from "remotion";
import { BonhomiaPromo } from "./BonhomiaPromo.jsx";

const defaultProps = {
  width: 1080,
  height: 1920,
  fps: 30,
  durationSeconds: 60,
  metadata: {
    title: "Bonhomia Suites",
    voiceDisclosure: "Voz generada con IA."
  },
  brand: {
    name: "Bonhomia Suites",
    publicBaseUrl: "https://bonhomiasuites.com"
  },
  scenes: [],
  logo: null,
  narrationAudio: null,
  musicAudio: null,
  paymentOutcome: "no_capture",
  captureNotes: []
};

export const RemotionRoot = () => (
  <Composition
    id="BonhomiaPromo"
    component={BonhomiaPromo}
    durationInFrames={defaultProps.durationSeconds * defaultProps.fps}
    fps={defaultProps.fps}
    width={defaultProps.width}
    height={defaultProps.height}
    defaultProps={defaultProps}
  />
);
