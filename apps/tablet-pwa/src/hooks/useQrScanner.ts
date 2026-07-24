import { useEffect, useRef, useState, type RefObject } from "react";
import jsQR from "jsqr";

/**
 * Decodes a QR code from the device camera. This is the realistic
 * pairing-discovery mechanism for a browser (there is no web-platform
 * API for browsing mDNS services) -- see docs/ARCHITECTURE.md. Manual
 * IP/code entry remains the fallback when camera access isn't available
 * or granted.
 */
export function useQrScanner(videoRef: RefObject<HTMLVideoElement>, active: boolean) {
  const [decodedText, setDecodedText] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const streamRef = useRef<MediaStream | null>(null);

  useEffect(() => {
    if (!active) return;
    let cancelled = false;
    let rafId = 0;
    const canvas = document.createElement("canvas");
    const ctx = canvas.getContext("2d");

    async function start() {
      // Verified on real hardware: browsers only expose
      // navigator.mediaDevices in a "secure context" (HTTPS or
      // localhost). This system is reached over plain http://<lan-ip>
      // by design (see docs/ARCHITECTURE.md -- no TLS in Phase 1), so
      // camera access is fundamentally unavailable here, not just
      // denied. Checking up front avoids a cryptic
      // "undefined is not an object (evaluating
      // 'navigator.mediaDevices.getUserMedia')" TypeError and gives a
      // message that actually explains what to do instead.
      if (!navigator.mediaDevices?.getUserMedia) {
        setError("Camera scanning needs a secure (HTTPS) connection, which this LAN setup doesn't use -- please use manual entry instead.");
        return;
      }
      try {
        const stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: "environment" } });
        if (cancelled) {
          stream.getTracks().forEach((t) => t.stop());
          return;
        }
        streamRef.current = stream;
        const video = videoRef.current;
        if (!video) return;
        video.srcObject = stream;
        await video.play();
        tick();
      } catch (err) {
        setError(err instanceof Error ? err.message : "Camera access was denied or unavailable");
      }
    }

    function tick() {
      if (cancelled) return;
      const video = videoRef.current;
      if (video && ctx && video.readyState === video.HAVE_ENOUGH_DATA) {
        canvas.width = video.videoWidth;
        canvas.height = video.videoHeight;
        ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
        const imageData = ctx.getImageData(0, 0, canvas.width, canvas.height);
        const code = jsQR(imageData.data, imageData.width, imageData.height);
        if (code) {
          setDecodedText(code.data);
          return; // stop scanning once found
        }
      }
      rafId = requestAnimationFrame(tick);
    }

    void start();

    return () => {
      cancelled = true;
      cancelAnimationFrame(rafId);
      streamRef.current?.getTracks().forEach((t) => t.stop());
      streamRef.current = null;
    };
  }, [active, videoRef]);

  return { decodedText, error };
}
