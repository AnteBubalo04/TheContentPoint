    const KEY = "__XFRAME_DEVICE_SINGLETON__";
    const prev = window[KEY];
    if (prev && typeof prev.stop === "function") {
        try { prev.stop(); } catch { }
    }

    let overlayLoopVideo = null;   // /overlay1.webm
    let overlayHeroVideo = null;   // /overlay2.webm
    let qrImg = null;
    let qrEl = null;

    let cameraVideo = null;
    let cameraStream = null;
    let imageCapture = null;

    let canvasEl = null;
    let ctx = null;

    let countdownInterval = null;
    let heroStopTimeoutId = null;
    let heroCleanupTimeoutId = null;

    const COUNTDOWN_SECONDS = 5;
    const FADE_MS = 180;

    let mediaUnlocked = false;
    let currentFlowId = 0;

    let heroActive = false;
    let pendingHeroRequest = null;
    let captureInFlight = false;

    let configuredApiBaseUrl = "";

    async function loadClientConfig() {
        try {
            const res = await fetch("/appsettings.json", { cache: "no-store" });
            const cfg = await res.json();
            configuredApiBaseUrl = (cfg.ApiBaseUrl || "").replace(/\/+$/, "");
        } catch {
            configuredApiBaseUrl = "";
        }
    }

    function getApiBaseUrl() {
        return configuredApiBaseUrl;
    }

    function stopAll() {
        if (countdownInterval) {
            try { clearInterval(countdownInterval); } catch { }
            countdownInterval = null;
        }

        if (heroStopTimeoutId) {
            try { clearTimeout(heroStopTimeoutId); } catch { }
            heroStopTimeoutId = null;
        }

        if (heroCleanupTimeoutId) {
            try { clearTimeout(heroCleanupTimeoutId); } catch { }
            heroCleanupTimeoutId = null;
        }

        try {
            if (overlayLoopVideo) overlayLoopVideo.pause();
            if (overlayHeroVideo) overlayHeroVideo.pause();
        } catch { }

        try {
            if (cameraStream) {
                const tracks = cameraStream.getTracks ? cameraStream.getTracks() : [];
                tracks.forEach(t => { try { t.stop(); } catch { } });
            }
        } catch { }

        cameraStream = null;
        imageCapture = null;
        mediaUnlocked = false;
        heroActive = false;
        pendingHeroRequest = null;
        captureInFlight = false;
        currentFlowId++;

        hideCountdown();
        setQROpacity(1);
        setLoopOpacity(1, false);
        setHeroOpacity(0, false);
    }

    window[KEY] = { stop: stopAll };

    function getCanvas() {
        return document.getElementById("canvas");
    }

    function ensureCanvasAndCtx() {
        const c = getCanvas();
        if (!c) {
            canvasEl = null;
            ctx = null;
            return false;
        }

        canvasEl = c;
        ctx = canvasEl.getContext("2d");
        return !!ctx;
    }

    function resizeCanvas() {
        if (!ensureCanvasAndCtx()) return;

        const ratio = Math.min(window.devicePixelRatio || 1, 2);
        canvasEl.width = Math.round(window.innerWidth * ratio);
        canvasEl.height = Math.round(window.innerHeight * ratio);

        canvasEl.style.width = `${window.innerWidth}px`;
        canvasEl.style.height = `${window.innerHeight}px`;

        canvasEl.style.position = "fixed";
        canvasEl.style.left = "-99999px";
        canvasEl.style.top = "-99999px";
        canvasEl.style.width = "1px";
        canvasEl.style.height = "1px";
        canvasEl.style.pointerEvents = "none";
        canvasEl.style.opacity = "0";

        ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
        styleCountdownEl();
        styleQrEl();
    }

    window.addEventListener("resize", resizeCanvas);

    function drawVideoCover(targetCtx, videoEl, dx, dy, dw, dh) {
        const vw = videoEl.videoWidth;
        const vh = videoEl.videoHeight;
        if (!vw || !vh) return;

        const videoAR = vw / vh;
        const destAR = dw / dh;

        let sx = 0, sy = 0, sw = vw, sh = vh;

        if (videoAR > destAR) {
            sh = vh;
            sw = Math.round(vh * destAR);
            sx = Math.round((vw - sw) / 2);
        } else {
            sw = vw;
            sh = Math.round(vw / destAR);
            sy = Math.round((vh - sh) / 2);
        }

        targetCtx.drawImage(videoEl, sx, sy, sw, sh, dx, dy, dw, dh);
    }

    function drawBitmapCover(targetCtx, bmp, dx, dy, dw, dh) {
        const vw = bmp.width;
        const vh = bmp.height;
        if (!vw || !vh) return;

        const videoAR = vw / vh;
        const destAR = dw / dh;

        let sx = 0, sy = 0, sw = vw, sh = vh;

        if (videoAR > destAR) {
            sh = vh;
            sw = Math.round(vh * destAR);
            sx = Math.round((vw - sw) / 2);
        } else {
            sw = vw;
            sh = Math.round(vw / destAR);
            sy = Math.round((vh - sh) / 2);
        }

        targetCtx.drawImage(bmp, sx, sy, sw, sh, dx, dy, dw, dh);
    }

    function createLayerVideo(id, src, zIndex, loop) {
        const v = document.getElementById(id) || document.createElement("video");
        v.id = id;
        v.src = v.src || src;
        v.autoplay = false;
        v.loop = loop;
        v.muted = true;
        v.playsInline = true;
        v.preload = "auto";
        v.setAttribute("playsinline", "");
        v.setAttribute("muted", "");
        v.setAttribute("preload", "auto");

        v.style.position = "fixed";
        v.style.left = "0";
        v.style.top = "0";
        v.style.width = "100vw";
        v.style.height = "100vh";
        v.style.objectFit = "cover";
        v.style.pointerEvents = "none";
        v.style.zIndex = String(zIndex);
        v.style.opacity = "0";
        v.style.transition = `opacity ${FADE_MS}ms linear`;
        v.style.willChange = "opacity";
        v.style.transform = "translateZ(0)";
        v.style.backfaceVisibility = "hidden";
        v.style.background = "transparent";

        if (!v.parentNode) {
            document.body.appendChild(v);
        }

        if (!v.getAttribute("data-loaded-src")) {
            v.load();
            v.setAttribute("data-loaded-src", "1");
        }

        return v;
    }

    function createQrEl() {
        const img = document.getElementById("xframeQrOverlay") || document.createElement("img");
        img.id = "xframeQrOverlay";
        img.src = "/qr.png";
        img.alt = "QR";
        img.style.position = "fixed";
        img.style.left = "50%";
        img.style.top = "50%";
        img.style.transform = "translate(-50%, -50%)";
        img.style.width = "min(40vw, 40vh)";
        img.style.height = "min(40vw, 40vh)";
        img.style.objectFit = "contain";
        img.style.pointerEvents = "none";
        img.style.zIndex = "10000";
        img.style.opacity = "1";
        img.style.transition = `opacity ${FADE_MS}ms linear`;
        img.style.willChange = "opacity";
        img.style.borderRadius = "16px";

        if (!img.parentNode) {
            document.body.appendChild(img);
        }

        return img;
    }

    function styleQrEl() {
        if (!qrEl) return;
        qrEl.style.width = "min(40vw, 40vh)";
        qrEl.style.height = "min(40vw, 40vh)";
    }

    function getCountdownEl() {
        return document.getElementById("countdown");
    }

    function styleCountdownEl() {
        const el = getCountdownEl();
        if (!el) return;

        el.style.position = "fixed";
        el.style.left = "50%";
        el.style.top = "50%";
        el.style.transform = "translate(-50%, -50%)";
        el.style.width = "clamp(140px, 22vw, 280px)";
        el.style.height = "clamp(140px, 22vw, 280px)";
        el.style.borderRadius = "9999px";
        el.style.display = "flex";
        el.style.alignItems = "center";
        el.style.justifyContent = "center";
        el.style.fontFamily = "Arial, sans-serif";
        el.style.fontWeight = "700";
        el.style.fontSize = "clamp(54px, 9vw, 120px)";
        el.style.lineHeight = "1";
        el.style.color = "#fff";
        el.style.background = "rgba(0,0,0,0.28)";
        el.style.border = "4px solid rgba(255,255,255,0.82)";
        el.style.backdropFilter = "blur(2px)";
        el.style.webkitBackdropFilter = "blur(2px)";
        el.style.zIndex = "10001";
        el.style.pointerEvents = "none";
        el.style.boxSizing = "border-box";
        el.style.textShadow = "0 2px 10px rgba(0,0,0,0.35)";
        el.style.opacity = "1";
    }

    function showCountdown(value) {
        const el = getCountdownEl();
        if (!el) return;
        styleCountdownEl();
        el.innerText = String(value);
        el.classList.remove("hidden");
    }

    function updateCountdown(value) {
        const el = getCountdownEl();
        if (!el) return;
        el.innerText = String(value);
    }

    function hideCountdown() {
        const el = getCountdownEl();
        if (!el) return;
        el.classList.add("hidden");
    }

    function setOpacity(el, value, animate = true) {
        if (!el) return;
        el.style.transition = animate ? `opacity ${FADE_MS}ms linear` : "none";
        el.style.opacity = String(value);
    }

    function setLoopOpacity(value, animate = true) {
        setOpacity(overlayLoopVideo, value, animate);
    }

    function setHeroOpacity(value, animate = true) {
        setOpacity(overlayHeroVideo, value, animate);
    }

    function setQROpacity(value, animate = true) {
        setOpacity(qrEl, value, animate);
    }

    function waitForVideoReady(v, timeoutMs = 5000) {
        if (!v) return Promise.reject(new Error("video null"));
        if (v.readyState >= 2) return Promise.resolve();

        return new Promise((resolve, reject) => {
            const t = setTimeout(() => {
                cleanup();
                reject(new Error("timeout waiting for video ready"));
            }, timeoutMs);

            const ok = () => { cleanup(); resolve(); };
            const bad = () => { cleanup(); reject(new Error("video error")); };

            function cleanup() {
                clearTimeout(t);
                v.removeEventListener("loadeddata", ok);
                v.removeEventListener("canplay", ok);
                v.removeEventListener("canplaythrough", ok);
                v.removeEventListener("error", bad);
            }

            v.addEventListener("loadeddata", ok, { once: true });
            v.addEventListener("canplay", ok, { once: true });
            v.addEventListener("canplaythrough", ok, { once: true });
            v.addEventListener("error", bad, { once: true });
        });
    }

    async function ensureVideoPlaying(v) {
        if (!v) return false;
        try {
            const p = v.play();
            if (p && typeof p.then === "function") await p;
            return true;
        } catch {
            return false;
        }
    }

    function ensureOverlayAndQR() {
        if (!cameraVideo) {
            cameraVideo = createLayerVideo("xframeCameraVideo", "", 9996, false);
            cameraVideo.removeAttribute("src");
            cameraVideo.srcObject = null;
            cameraVideo.style.opacity = "1";
            cameraVideo.style.transition = "none";
            cameraVideo.style.left = "50.8%";
            cameraVideo.style.top = "44%";
            cameraVideo.style.width = "67vw";
            cameraVideo.style.height = "74vh";
            cameraVideo.style.transform = "translate(-50%, -50%) scaleX(-1)";
            cameraVideo.style.objectFit = "cover";
            cameraVideo.style.background = "#000";
            cameraVideo.style.webkitMaskImage = "linear-gradient(to bottom, rgba(0,0,0,0) 0%, rgba(0,0,0,0.78) 7%, rgba(0,0,0,1) 13%, rgba(0,0,0,1) 84%, rgba(0,0,0,0.82) 92%, rgba(0,0,0,0) 100%)";
            cameraVideo.style.maskImage = "linear-gradient(to bottom, rgba(0,0,0,0) 0%, rgba(0,0,0,0.78) 7%, rgba(0,0,0,1) 13%, rgba(0,0,0,1) 84%, rgba(0,0,0,0.82) 92%, rgba(0,0,0,0) 100%)";
        }

        if (!overlayLoopVideo) {
            overlayLoopVideo = createLayerVideo("xframeOverlayLoop", "/overlay1.webm", 9998, true);
            overlayLoopVideo.onended = () => {
                try {
                    overlayLoopVideo.currentTime = 0;
                    overlayLoopVideo.play();
                } catch { }
            };
        }

        if (!overlayHeroVideo) {
            overlayHeroVideo = createLayerVideo("xframeOverlayHero", "/overlay2.webm", 9999, false);

            overlayHeroVideo.onended = () => {
                finishFlowToIdle();
            };

            overlayHeroVideo.onerror = () => {
                finishFlowToIdle();
            };
        }

        if (!qrEl) {
            qrEl = createQrEl();
        }

        if (!qrImg) {
            qrImg = new Image();
            qrImg.src = "/qr.png";
        }
    }

    async function ensureCameraStarted() {
        ensureOverlayAndQR();

        if (cameraStream && cameraVideo.srcObject && cameraVideo.videoWidth > 0) return true;

        cameraStream = await navigator.mediaDevices.getUserMedia({
            video: {
                facingMode: "user",
                width: { ideal: 1280 },
                height: { ideal: 720 },
                aspectRatio: { ideal: 16 / 9 }
            },
            audio: false
        });

        try {
            const track = cameraStream.getVideoTracks ? cameraStream.getVideoTracks()[0] : null;
            if (track && typeof ImageCapture !== "undefined") {
                imageCapture = new ImageCapture(track);
            } else {
                imageCapture = null;
            }
        } catch {
            imageCapture = null;
        }

        cameraVideo.srcObject = cameraStream;
        await cameraVideo.play();

        await new Promise((resolve, reject) => {
            const t0 = performance.now();
            const tick = () => {
                if (cameraVideo.videoWidth > 0 && cameraVideo.videoHeight > 0) return resolve();
                if (performance.now() - t0 > 6000) return reject(new Error("Timeout waiting for camera frames"));
                requestAnimationFrame(tick);
            };
            tick();
        });

        return true;
    }

    async function ensureLoopReadyAndPlaying() {
        if (!overlayLoopVideo) return false;

        try { await waitForVideoReady(overlayLoopVideo, 6000); } catch { }

        const ok = await ensureVideoPlaying(overlayLoopVideo);
        return ok;
    }

    async function finishFlowToIdle() {
        heroActive = false;

        if (heroStopTimeoutId) {
            clearTimeout(heroStopTimeoutId);
            heroStopTimeoutId = null;
        }

        if (heroCleanupTimeoutId) {
            clearTimeout(heroCleanupTimeoutId);
            heroCleanupTimeoutId = null;
        }

        hideCountdown();

        setHeroOpacity(0, true);
        setLoopOpacity(1, true);
        setQROpacity(1, true);

        if (mediaUnlocked) {
            ensureLoopReadyAndPlaying().catch(() => { });
        }
    }

    async function uploadPhotoBlob(sessionId, blob, flowId) {
        if (!blob) return;
        if (flowId !== currentFlowId) return;

        const fd = new FormData();
        fd.append("photo", blob, `${sessionId}.jpg`);

        fetch(`${getApiBaseUrl()}/api/session/${sessionId}/photo`, {
            method: "POST",
            body: fd,
            cache: "no-store"
        }).catch(() => { });
    }

    async function capturePhotoBlobFast() {
        if (imageCapture && typeof imageCapture.takePhoto === "function") {
            try {
                const blob = await imageCapture.takePhoto();
                if (blob && blob.size > 0) {
                    return blob;
                }
            } catch {
            }
        }

        if (!cameraVideo) return null;

        const outW = cameraVideo.videoWidth || 1280;
        const outH = cameraVideo.videoHeight || 720;

        if (typeof createImageBitmap === "function") {
            let bmp = null;

            try {
                bmp = await createImageBitmap(cameraVideo);

                if (typeof OffscreenCanvas !== "undefined") {
                    const offscreen = new OffscreenCanvas(outW, outH);
                    const offctx = offscreen.getContext("2d", { alpha: false });
                    if (!offctx) return null;

                    offctx.drawImage(bmp, 0, 0, outW, outH);
                    const blob = await offscreen.convertToBlob({
                        type: "image/jpeg",
                        quality: 0.92
                    });

                    return blob;
                }
            } catch {
            } finally {
                try { if (bmp && typeof bmp.close === "function") bmp.close(); } catch { }
            }
        }

        const snap = document.createElement("canvas");
        snap.width = outW;
        snap.height = outH;

        const sctx = snap.getContext("2d", { alpha: false });
        if (!sctx) return null;

        try {
            sctx.drawImage(cameraVideo, 0, 0, outW, outH);
        } catch {
            return null;
        }

        return await new Promise(resolve => {
            snap.toBlob(blob => resolve(blob || null), "image/jpeg", 0.92);
        });
    }

    async function sendCameraSnapshot(sessionId, flowId) {
        if (flowId !== currentFlowId) return;
        if (!mediaUnlocked) return;
        if (!cameraVideo) return;
        if (captureInFlight) return;

        captureInFlight = true;

        try {
            await new Promise(resolve => requestAnimationFrame(resolve));

            if (flowId !== currentFlowId) return;

            const blob = await capturePhotoBlobFast();
            if (!blob) return;
            if (flowId !== currentFlowId) return;

            uploadPhotoBlob(sessionId, blob, flowId).catch(() => { });
        } finally {
            captureInFlight = false;
        }
    }

    async function playHeroNow(flowId) {
        if (!overlayHeroVideo) return;
        if (flowId !== currentFlowId) return;

        if (heroStopTimeoutId) {
            clearTimeout(heroStopTimeoutId);
            heroStopTimeoutId = null;
        }

        if (heroCleanupTimeoutId) {
            clearTimeout(heroCleanupTimeoutId);
            heroCleanupTimeoutId = null;
        }

        try { overlayHeroVideo.currentTime = 0; } catch { }

        await ensureVideoPlaying(overlayHeroVideo);

        if (flowId !== currentFlowId) return;

        heroActive = true;

        setLoopOpacity(0, true);
        setHeroOpacity(1, true);

        const durSec = overlayHeroVideo.duration;
        const fallbackMs = 9000;
        const heroMs = (Number.isFinite(durSec) && durSec > 0)
            ? Math.round(durSec * 1000)
            : fallbackMs;

        heroStopTimeoutId = setTimeout(() => {
            finishFlowToIdle();
            heroStopTimeoutId = null;
        }, heroMs + 300);
    }

    async function runPendingHeroIfAny() {
        if (!pendingHeroRequest) return;
        const req = pendingHeroRequest;
        pendingHeroRequest = null;
        await internalStartCameraWithSmoothTransition(req.sessionId, req.flowId, true);
    }

    async function internalStartCameraWithSmoothTransition(sessionId, flowId, fromPending = false) {
        if (flowId !== currentFlowId) return;

        if (countdownInterval) {
            clearInterval(countdownInterval);
            countdownInterval = null;
        }

        if (heroStopTimeoutId) {
            clearTimeout(heroStopTimeoutId);
            heroStopTimeoutId = null;
        }

        if (!mediaUnlocked) {
            pendingHeroRequest = { sessionId, flowId };
            setQROpacity(0, true);
            return;
        }

        setQROpacity(0, true);

        await ensureLoopReadyAndPlaying();
        await playHeroNow(flowId);

        if (flowId !== currentFlowId) return;

        showCountdown(COUNTDOWN_SECONDS);

        let t = COUNTDOWN_SECONDS;

        countdownInterval = setInterval(() => {
            if (flowId !== currentFlowId) {
                clearInterval(countdownInterval);
                countdownInterval = null;
                return;
            }

            t--;

            if (t > 0) {
                updateCountdown(t);
            }

            if (t <= 0) {
                clearInterval(countdownInterval);
                countdownInterval = null;

                hideCountdown();

                setTimeout(() => {
                    sendCameraSnapshot(sessionId, flowId).catch(() => { });
                }, 0);
            }
        }, 1000);
    }

    async function unlockMediaOnce() {
        if (mediaUnlocked) return;
        mediaUnlocked = true;

        try { await ensureCameraStarted(); } catch { }
        try { await ensureLoopReadyAndPlaying(); } catch { }

        setLoopOpacity(1, false);
        setHeroOpacity(0, false);
        setQROpacity(1, false);

        await runPendingHeroIfAny();
    }

    document.addEventListener("pointerdown", unlockMediaOnce, { capture: true, once: true });
    document.addEventListener("click", unlockMediaOnce, { capture: true, once: true });

    window.startCameraWithSmoothTransition = async (sessionId) => {
        currentFlowId++;
        const flowId = currentFlowId;
        await internalStartCameraWithSmoothTransition(sessionId, flowId, false);
    };

    window.drawOverlayOnly = async () => {
        currentFlowId++;

        if (countdownInterval) {
            clearInterval(countdownInterval);
            countdownInterval = null;
        }

        if (heroStopTimeoutId) {
            clearTimeout(heroStopTimeoutId);
            heroStopTimeoutId = null;
        }

        if (heroCleanupTimeoutId) {
            clearTimeout(heroCleanupTimeoutId);
            heroCleanupTimeoutId = null;
        }

        ensureOverlayAndQR();

        heroActive = false;
        pendingHeroRequest = null;
        captureInFlight = false;

        hideCountdown();

        try { if (overlayHeroVideo) overlayHeroVideo.pause(); } catch { }

        try {
            if (mediaUnlocked) {
                await ensureLoopReadyAndPlaying();
            }
        } catch { }

        setHeroOpacity(0, false);
        setLoopOpacity(1, false);
        setQROpacity(1, false);
    };

    async function boot() {
        await loadClientConfig();
        ensureOverlayAndQR();

        const t0 = performance.now();
        while (!getCanvas()) {
            if (performance.now() - t0 > 5000) break;
            await new Promise(r => setTimeout(r, 50));
        }

        resizeCanvas();
        styleCountdownEl();

        setHeroOpacity(0, false);
        setLoopOpacity(1, false);
        setQROpacity(1, false);
    }

    window.addEventListener("load", boot);
