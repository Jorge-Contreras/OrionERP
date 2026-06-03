import React from "react";
import {
  AbsoluteFill,
  Audio,
  Img,
  Sequence,
  interpolate,
  spring,
  staticFile,
  useCurrentFrame,
  useVideoConfig
} from "remotion";

const palette = {
  ink: "#172622",
  muted: "#d9e0d8",
  teal: "#0f4c5c",
  tealDark: "#083640",
  gold: "#c99d4a",
  clay: "#9d5941",
  warm: "#fbfaf6",
  white: "#ffffff"
};

export function BonhomiaPromo({
  scenes,
  logo,
  narrationAudio,
  musicAudio,
  metadata,
  paymentOutcome
}) {
  const frame = useCurrentFrame();
  const { fps, durationInFrames } = useVideoConfig();
  const time = frame / fps;
  const activeScene = scenes.find((scene) => time >= scene.start && time < scene.start + scene.duration)
    || scenes[scenes.length - 1]
    || null;

  return (
    <AbsoluteFill style={styles.root}>
      {scenes.map((scene) => (
        <Scene key={scene.id} scene={scene} logo={logo} />
      ))}

      <ProgressBar scenes={scenes} frame={frame} fps={fps} />

      {activeScene ? (
        <Caption
          scene={activeScene}
          paymentOutcome={paymentOutcome}
          totalFrames={durationInFrames}
        />
      ) : null}

      {musicAudio ? <Audio src={staticFile(musicAudio)} volume={0.13} /> : null}
      {narrationAudio ? <Audio src={staticFile(narrationAudio)} volume={1.08} /> : null}

      <div style={styles.disclosure}>
        {metadata?.voiceDisclosure || "Voz generada con IA."}
      </div>
    </AbsoluteFill>
  );
}

function Scene({ scene, logo }) {
  const { fps } = useVideoConfig();
  const durationFrames = Math.round(scene.duration * fps);

  return (
    <Sequence from={Math.round(scene.start * fps)} durationInFrames={durationFrames}>
      <SceneFrame scene={scene} logo={logo} durationFrames={durationFrames} />
    </Sequence>
  );
}

function SceneFrame({ scene, logo, durationFrames }) {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const intro = spring({
    frame,
    fps,
    config: {
      damping: 18,
      stiffness: 90
    }
  });
  const scale = interpolate(frame, [0, durationFrames], [1.06, 1.16], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp"
  });
  const sourceBadge = scene.source === "capture" ? "Sitio real" : "Imagen Bonhomia";
  const isCapture = scene.source === "capture";

  return (
    <AbsoluteFill style={styles.scene}>
      <AbsoluteFill>
        <Img
          src={staticFile(scene.asset)}
          style={{
            ...(isCapture ? styles.captureBackdrop : styles.background),
            transform: `scale(${scale})`
          }}
        />
        <div style={isCapture ? styles.captureScrim : styles.scrim} />
      </AbsoluteFill>

      <div style={styles.topBar}>
        {logo ? <Img src={staticFile(logo)} style={styles.logo} /> : <div style={styles.logoFallback}>B</div>}
        <div>
          <div style={styles.brand}>Bonhomia Suites</div>
          <div style={styles.brandSub}>Reserva directa</div>
        </div>
        <div style={styles.badge}>{sourceBadge}</div>
      </div>

      {isCapture ? (
        <CaptureLayout scene={scene} intro={intro} />
      ) : (
        <div
          style={{
            ...styles.copy,
            opacity: intro,
            transform: `translateY(${interpolate(intro, [0, 1], [46, 0])}px)`
          }}
        >
          <div style={styles.kicker}>{scene.kicker}</div>
          <h1 style={styles.title}>{scene.title}</h1>
          <p style={styles.body}>{scene.body}</p>
        </div>
      )}
    </AbsoluteFill>
  );
}

function CaptureLayout({ scene, intro }) {
  return (
    <>
      <div
        style={{
          ...styles.phoneStage,
          opacity: intro,
          transform: `translateY(${interpolate(intro, [0, 1], [30, 0])}px)`
        }}
      >
        <div style={styles.phoneFrame}>
          <div style={styles.phoneSpeaker} />
          <Img src={staticFile(scene.asset)} style={styles.phoneScreen} />
        </div>
      </div>

      <div
        style={{
          ...styles.captureCopy,
          opacity: intro,
          transform: `translateY(${interpolate(intro, [0, 1], [42, 0])}px)`
        }}
      >
        <div style={styles.kicker}>{scene.kicker}</div>
        <h1 style={styles.captureTitle}>{scene.title}</h1>
        <p style={styles.captureBody}>{scene.body}</p>
      </div>
    </>
  );
}

function ProgressBar({ scenes, frame, fps }) {
  return (
    <div style={styles.progressWrap}>
      {scenes.map((scene) => {
        const start = scene.start * fps;
        const end = (scene.start + scene.duration) * fps;
        const progress = interpolate(frame, [start, end], [0, 1], {
          extrapolateLeft: "clamp",
          extrapolateRight: "clamp"
        });

        return (
          <div key={scene.id} style={styles.progressTrack}>
            <div style={{ ...styles.progressFill, width: `${progress * 100}%` }} />
          </div>
        );
      })}
    </div>
  );
}

function Caption({ scene, paymentOutcome }) {
  const isPaymentScene = scene.id === "payment" && paymentOutcome !== "completed";
  const message = isPaymentScene
    ? "PayPal abre su checkout seguro; con credenciales sandbox se captura tambien la confirmacion final."
    : scene.voiceover;

  return (
    <div style={styles.caption}>
      <span style={styles.captionDot} />
      <span>{message}</span>
    </div>
  );
}

const styles = {
  root: {
    backgroundColor: palette.tealDark,
    color: palette.white,
    fontFamily: "Segoe UI, Inter, Arial, sans-serif"
  },
  scene: {
    overflow: "hidden"
  },
  background: {
    width: "100%",
    height: "100%",
    objectFit: "cover",
    filter: "saturate(1.08) contrast(1.02)"
  },
  captureBackdrop: {
    width: "100%",
    height: "100%",
    objectFit: "cover",
    filter: "blur(24px) saturate(1.15) contrast(0.98)",
    opacity: 0.72
  },
  scrim: {
    position: "absolute",
    inset: 0,
    background: "linear-gradient(180deg, rgba(5, 20, 22, 0.42) 0%, rgba(5, 20, 22, 0.16) 38%, rgba(5, 20, 22, 0.86) 100%)"
  },
  captureScrim: {
    position: "absolute",
    inset: 0,
    background: "linear-gradient(180deg, rgba(8, 54, 64, 0.62) 0%, rgba(23, 38, 34, 0.22) 42%, rgba(8, 54, 64, 0.92) 100%)"
  },
  topBar: {
    position: "absolute",
    top: 64,
    left: 58,
    right: 58,
    display: "grid",
    gridTemplateColumns: "82px minmax(0, 1fr) auto",
    gap: 20,
    alignItems: "center"
  },
  logo: {
    width: 82,
    height: 82,
    objectFit: "contain",
    borderRadius: 8,
    backgroundColor: "rgba(255, 255, 255, 0.88)",
    padding: 8
  },
  logoFallback: {
    display: "grid",
    placeItems: "center",
    width: 82,
    height: 82,
    borderRadius: 8,
    backgroundColor: palette.gold,
    color: palette.ink,
    fontSize: 44,
    fontWeight: 900
  },
  brand: {
    fontSize: 34,
    fontWeight: 900,
    lineHeight: 1
  },
  brandSub: {
    marginTop: 7,
    color: palette.muted,
    fontSize: 22,
    fontWeight: 700
  },
  badge: {
    padding: "12px 18px",
    border: "1px solid rgba(255, 255, 255, 0.3)",
    borderRadius: 999,
    backgroundColor: "rgba(15, 76, 92, 0.76)",
    fontSize: 20,
    fontWeight: 900,
    textTransform: "uppercase"
  },
  copy: {
    position: "absolute",
    left: 72,
    right: 72,
    bottom: 338
  },
  kicker: {
    display: "inline-block",
    marginBottom: 20,
    padding: "10px 16px",
    borderRadius: 999,
    backgroundColor: "rgba(201, 157, 74, 0.95)",
    color: "#201710",
    fontSize: 24,
    fontWeight: 950,
    textTransform: "uppercase"
  },
  title: {
    margin: 0,
    color: palette.white,
    fontFamily: "Georgia, Times New Roman, serif",
    fontSize: 84,
    fontWeight: 700,
    lineHeight: 0.96,
    letterSpacing: 0,
    textShadow: "0 12px 36px rgba(0, 0, 0, 0.42)"
  },
  body: {
    maxWidth: 860,
    margin: "24px 0 0",
    color: "rgba(255, 255, 255, 0.92)",
    fontSize: 34,
    fontWeight: 720,
    lineHeight: 1.22,
    textShadow: "0 8px 26px rgba(0, 0, 0, 0.42)"
  },
  phoneStage: {
    position: "absolute",
    top: 166,
    left: 0,
    right: 0,
    display: "grid",
    placeItems: "center"
  },
  phoneFrame: {
    position: "relative",
    width: 628,
    height: 1358,
    padding: "42px 18px 22px",
    border: "5px solid rgba(255, 255, 255, 0.92)",
    borderRadius: 54,
    background: "linear-gradient(180deg, rgba(255, 255, 255, 0.97), rgba(234, 239, 235, 0.97))",
    boxShadow: "0 42px 90px rgba(0, 0, 0, 0.35)"
  },
  phoneSpeaker: {
    position: "absolute",
    top: 18,
    left: "50%",
    width: 92,
    height: 10,
    borderRadius: 999,
    backgroundColor: "rgba(23, 38, 34, 0.25)",
    transform: "translateX(-50%)"
  },
  phoneScreen: {
    width: "100%",
    height: "100%",
    objectFit: "contain",
    borderRadius: 34,
    backgroundColor: "#fbfaf6"
  },
  captureCopy: {
    position: "absolute",
    left: 58,
    right: 58,
    bottom: 142,
    padding: "26px 30px",
    border: "1px solid rgba(255, 255, 255, 0.22)",
    borderRadius: 8,
    background: "rgba(8, 54, 64, 0.88)",
    boxShadow: "0 22px 54px rgba(0, 0, 0, 0.24)"
  },
  captureTitle: {
    margin: 0,
    color: palette.white,
    fontFamily: "Georgia, Times New Roman, serif",
    fontSize: 64,
    fontWeight: 700,
    lineHeight: 0.98,
    letterSpacing: 0
  },
  captureBody: {
    margin: "14px 0 0",
    color: "rgba(255, 255, 255, 0.9)",
    fontSize: 27,
    fontWeight: 760,
    lineHeight: 1.18
  },
  progressWrap: {
    position: "absolute",
    left: 58,
    right: 58,
    bottom: 92,
    display: "grid",
    gridTemplateColumns: "repeat(9, minmax(0, 1fr))",
    gap: 8
  },
  progressTrack: {
    height: 8,
    overflow: "hidden",
    borderRadius: 999,
    backgroundColor: "rgba(255, 255, 255, 0.24)"
  },
  progressFill: {
    height: "100%",
    borderRadius: 999,
    backgroundColor: palette.gold
  },
  caption: {
    position: "absolute",
    left: 58,
    right: 58,
    bottom: 20,
    display: "grid",
    gridTemplateColumns: "22px minmax(0, 1fr)",
    gap: 18,
    alignItems: "start",
    padding: "18px 24px",
    borderRadius: 8,
    backgroundColor: "rgba(251, 250, 246, 0.94)",
    color: palette.ink,
    fontSize: 23,
    fontWeight: 840,
    lineHeight: 1.18,
    boxShadow: "0 22px 54px rgba(0, 0, 0, 0.26)"
  },
  captionDot: {
    width: 16,
    height: 16,
    marginTop: 10,
    borderRadius: 999,
    backgroundColor: palette.clay
  },
  disclosure: {
    position: "absolute",
    right: 58,
    top: 154,
    color: "rgba(255, 255, 255, 0.78)",
    fontSize: 18,
    fontWeight: 700
  }
};
