(() => {
    const KEY = "__XFRAME_DEVICE_SINGLETON__";
    const prev = window[KEY];
    if (prev && typeof prev.stop === "function") {
        try { prev.stop(); } catch { }
    }

    let overlayLoopVideoA = null;
    let overlayLoopVideoB = null;
    let overlayHeroVideo = null;
    let qrImg = null;
    let qrEl = null;

    let cameraVideo = null;
    let cameraStream = null;

    let canvasEl = null;
    let ctx = null;

    const COUNTDOWN_SECONDS = 5;
    const FADE_MS = 220;
    const LOOP_BLEND_MS = 160;
    const LOOP_SWAP_GUARD_MS = 80;
    const HERO_RETURN_PREP_MS = 900;

    let currentFlowId = 0;
    let heroActive = false;
    let pendingHeroRequest = null;

    let activeLoopVideo = null;
    let standbyLoopVideo = null;
    let loopSwapInProgress = false;
    let loopSwapTimer = null;

    let heroReturnPrepared = false;
    let heroFinishedResolve = null;
    let heroFinishedPromise = Promise.resolve();
    let loopPrepared = false;

    function sleep(ms) {
        return new Promise(resolve => setTimeout(resolve, ms));
    }

    function createDeferred() {
        let resolve;
        const promise = new Promise(r => { resolve = r; });
        return { promise, resolve };
    }

    async function startCamera() {
        if (cameraStream) {
            if (cameraVideo && cameraVideo.paused) {
                try { await cameraVideo.play(); } catch { }
            }
            return;
        }

        try {
            cameraStream = await navigator.mediaDevices.getUserMedia({
                video: {
                    facingMode: "user",
                    width: { ideal: 1080 },
                    height: { ideal: 1920 }
                },
                audio: false
            });

            if (cameraVideo) {
                cameraVideo.srcObject = cameraStream;
                try {
                    await cameraVideo.play();
                } catch { }
            }
        } catch (e) {
            console.error("Camera error:", e);
            throw e;
        }
    }

    function clearLoopSwapTimer() {
        if (loopSwapTimer) {
            try { clearTimeout(loopSwapTimer); } catch { }
            loopSwapTimer = null;
        }
    }

    function resolveHeroFinished() {
        const resolver = heroFinishedResolve;
        heroFinishedResolve = null;
        if (resolver) {
            try { resolver(); } catch { }
        }
    }

    function stopAll() {
        clearLoopSwapTimer();

        try {
            if (overlayLoopVideoA) overlayLoopVideoA.pause();
            if (overlayLoopVideoB) overlayLoopVideoB.pause();
            if (overlayHeroVideo) overlayHeroVideo.pause();
        } catch { }

        heroActive = false;
        pendingHeroRequest = null;
        currentFlowId++;
        loopSwapInProgress = false;
        heroReturnPrepared = false;
        loopPrepared = false;

        hideCountdown();

        setQROpacity(1, false);
        setLoopOpacity(overlayLoopVideoA, 0, false);
        setLoopOpacity(overlayLoopVideoB, 0, false);
        setHeroOpacity(0, false);

        activeLoopVideo = overlayLoopVideoA || null;
        standbyLoopVideo = overlayLoopVideoB || null;

        resolveHeroFinished();
        heroFinishedPromise = Promise.resolve();
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

    function createLayerVideo(id, src, zIndex, loop) {
        const v = document.getElementById(id) || document.createElement("video");
        v.id = id;

        if (src) {
            v.src = src;
        } else {
            v.removeAttribute("src");
        }

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

        if (src) {
            v.load();
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

    function setOpacity(el, value, animate = true, durationMs = FADE_MS) {
        if (!el) return;
        el.style.transition = animate ? `opacity ${durationMs}ms linear` : "none";
        el.style.opacity = String(value);
    }

    function setLoopOpacity(loopVideo, value, animate = true, durationMs = FADE_MS) {
        setOpacity(loopVideo, value, animate, durationMs);
    }

    function setHeroOpacity(value, animate = true, durationMs = FADE_MS) {
        setOpacity(overlayHeroVideo, value, animate, durationMs);
    }

    function setQROpacity(value, animate = true, durationMs = FADE_MS) {
        setOpacity(qrEl, value, animate, durationMs);
    }

    function ensureOverlayAndQR() {
        if (!cameraVideo) {
            cameraVideo = createLayerVideo("xframeCameraVideo", "", 9996, false);
            cameraVideo.removeAttribute("src");
            cameraVideo.srcObject = null;
            cameraVideo.style.opacity = "1";
            cameraVideo.style.transition = "none";

            startCamera().catch(err => console.error("Initial camera start failed:", err));
        }

        if (!overlayLoopVideoA) {
            overlayLoopVideoA = createLayerVideo("xframeOverlayLoopA", "/overlay1.webm", 9998, false);
        }

        if (!overlayLoopVideoB) {
            overlayLoopVideoB = createLayerVideo("xframeOverlayLoopB", "/overlay1.webm", 9998, false);
        }

        if (!overlayHeroVideo) {
            overlayHeroVideo = createLayerVideo("xframeOverlayHero", "/overlay2.webm", 9999, false);
            overlayHeroVideo.onended = () => {
                finalizeHeroToIdle();
            };
            overlayHeroVideo.onerror = () => {
                finalizeHeroToIdle();
            };
        }

        if (!qrEl) {
            qrEl = createQrEl();
        }

        if (!qrImg) {
            qrImg = new Image();
            qrImg.src = "/qr.png";
        }

        if (!activeLoopVideo) activeLoopVideo = overlayLoopVideoA;
        if (!standbyLoopVideo) standbyLoopVideo = overlayLoopVideoB;
    }

    async function waitForVideoReady(video, timeoutMs = 3000) {
        if (!video) return;
        if (video.readyState >= 2) return;

        await new Promise((resolve) => {
            let done = false;

            const finish = () => {
                if (done) return;
                done = true;
                cleanup();
                resolve();
            };

            const cleanup = () => {
                try { video.removeEventListener("loadeddata", finish); } catch { }
                try { video.removeEventListener("canplay", finish); } catch { }
                try { video.removeEventListener("canplaythrough", finish); } catch { }
                try { video.removeEventListener("loadedmetadata", finish); } catch { }
                try { clearTimeout(timer); } catch { }
            };

            const timer = setTimeout(finish, timeoutMs);

            video.addEventListener("loadeddata", finish, { once: true });
            video.addEventListener("canplay", finish, { once: true });
            video.addEventListener("canplaythrough", finish, { once: true });
            video.addEventListener("loadedmetadata", finish, { once: true });
        });
    }

    async function resetVideoToStart(video) {
        if (!video) return;
        try { video.pause(); } catch { }
        try {
            if (typeof video.fastSeek === "function") {
                video.fastSeek(0);
            } else {
                video.currentTime = 0;
            }
        } catch { }
        await waitForVideoReady(video, 1200);
    }

    async function playVideo(video) {
        if (!video) return;
        try {
            video.muted = true;
            video.playsInline = true;
            await video.play();
        } catch (e) {
            console.warn("Video play failed:", e);
        }
    }

    async function ensureHeroPrepared() {
        if (!overlayHeroVideo) return;
        await resetVideoToStart(overlayHeroVideo);
    }

    async function prepareLoopPair(force = false) {
        ensureOverlayAndQR();

        if (loopPrepared && !force) {
            scheduleLoopSwap();
            return;
        }

        await waitForVideoReady(overlayLoopVideoA, 2000);
        await waitForVideoReady(overlayLoopVideoB, 2000);

        activeLoopVideo = overlayLoopVideoA;
        standbyLoopVideo = overlayLoopVideoB;

        await resetVideoToStart(activeLoopVideo);
        await resetVideoToStart(standbyLoopVideo);

        setLoopOpacity(activeLoopVideo, 1, false);
        setLoopOpacity(standbyLoopVideo, 0, false);

        await playVideo(activeLoopVideo);

        loopPrepared = true;
        scheduleLoopSwap();
    }

    function scheduleLoopSwap() {
        clearLoopSwapTimer();

        if (heroActive) return;
        if (!activeLoopVideo || !standbyLoopVideo) return;

        const duration = Number(activeLoopVideo.duration || 0);
        const currentTime = Number(activeLoopVideo.currentTime || 0);

        if (!duration || duration <= 0.2) {
            loopSwapTimer = setTimeout(scheduleLoopSwap, 120);
            return;
        }

        const remainingMs = Math.max(0, ((duration - currentTime) * 1000) - LOOP_BLEND_MS - LOOP_SWAP_GUARD_MS);
        loopSwapTimer = setTimeout(() => {
            performLoopSwap().catch(err => console.warn("Loop swap failed:", err));
        }, remainingMs);
    }

    async function performLoopSwap() {
        if (heroActive) return;
        if (loopSwapInProgress) return;
        if (!activeLoopVideo || !standbyLoopVideo) return;

        loopSwapInProgress = true;

        const oldFront = activeLoopVideo;
        const newFront = standbyLoopVideo;

        try {
            await resetVideoToStart(newFront);
            setLoopOpacity(newFront, 0, false);
            await playVideo(newFront);

            await new Promise(requestAnimationFrame);

            setLoopOpacity(newFront, 1, true, LOOP_BLEND_MS);
            setLoopOpacity(oldFront, 0, true, LOOP_BLEND_MS);

            await sleep(LOOP_BLEND_MS + 30);

            try { oldFront.pause(); } catch { }
            try { oldFront.currentTime = 0; } catch { }

            activeLoopVideo = newFront;
            standbyLoopVideo = oldFront;
        } finally {
            loopSwapInProgress = false;
            scheduleLoopSwap();
        }
    }

    async function drawOverlayOnly() {
        ensureOverlayAndQR();
        resizeCanvas();

        await startCamera();
        await prepareLoopPair();

        hideCountdown();
        heroActive = false;
        pendingHeroRequest = null;
        heroReturnPrepared = false;

        setHeroOpacity(0, false);
        setQROpacity(1, false);
    }

    async function transitionIdleToHero() {
        heroReturnPrepared = false;
        clearLoopSwapTimer();

        await ensureHeroPrepared();

        setHeroOpacity(0, false);
        await playVideo(overlayHeroVideo);

        await new Promise(requestAnimationFrame);

        if (activeLoopVideo) {
            setLoopOpacity(activeLoopVideo, 0, true, FADE_MS);
        }
        if (standbyLoopVideo) {
            setLoopOpacity(standbyLoopVideo, 0, false);
        }

        setHeroOpacity(1, true, FADE_MS);
        setQROpacity(0, true, FADE_MS);

        await sleep(FADE_MS + 20);

        try { if (activeLoopVideo) activeLoopVideo.pause(); } catch { }
        try { if (standbyLoopVideo) standbyLoopVideo.pause(); } catch { }
    }

    async function prepareIdleLoopForHeroReturn() {
        if (heroReturnPrepared) return;

        heroReturnPrepared = true;

        activeLoopVideo = overlayLoopVideoA;
        standbyLoopVideo = overlayLoopVideoB;

        setLoopOpacity(activeLoopVideo, 0, false);
        setLoopOpacity(standbyLoopVideo, 0, false);

        try { activeLoopVideo.pause(); } catch { }
        try {
            if (typeof activeLoopVideo.fastSeek === "function") {
                activeLoopVideo.fastSeek(0);
            } else {
                activeLoopVideo.currentTime = 0;
            }
        } catch { }

        await waitForVideoReady(activeLoopVideo, 1200);
        await playVideo(activeLoopVideo);

        (async () => {
            try {
                try { standbyLoopVideo.pause(); } catch { }
                try {
                    if (typeof standbyLoopVideo.fastSeek === "function") {
                        standbyLoopVideo.fastSeek(0);
                    } else {
                        standbyLoopVideo.currentTime = 0;
                    }
                } catch { }
                await waitForVideoReady(standbyLoopVideo, 1200);
            } catch { }
        })();

        loopPrepared = true;
    }

    async function monitorHeroForReturn(flowId) {
        while (heroActive && flowId === currentFlowId && overlayHeroVideo) {
            try {
                const duration = Number(overlayHeroVideo.duration || 0);
                const currentTime = Number(overlayHeroVideo.currentTime || 0);

                if (duration > 0.2) {
                    const remainingMs = Math.max(0, (duration - currentTime) * 1000);
                    if (remainingMs <= HERO_RETURN_PREP_MS) {
                        await prepareIdleLoopForHeroReturn();
                        return;
                    }
                }
            } catch {
            }

            await sleep(50);
        }
    }

    async function finalizeHeroToIdle() {
        if (!heroActive && !heroReturnPrepared) {
            resolveHeroFinished();
            return;
        }

        try {
            await prepareIdleLoopForHeroReturn();

            setLoopOpacity(activeLoopVideo, 1, true, FADE_MS);
            setQROpacity(1, true, FADE_MS);
            setHeroOpacity(0, true, FADE_MS);

            await sleep(FADE_MS + 20);

            try { overlayHeroVideo.pause(); } catch { }
            try { overlayHeroVideo.currentTime = 0; } catch { }

            heroActive = false;
            pendingHeroRequest = null;
            heroReturnPrepared = false;

            scheduleLoopSwap();
        } finally {
            resolveHeroFinished();
        }
    }

    function captureCurrentFrameDataUrl() {
        if (!cameraVideo || !cameraVideo.videoWidth || !cameraVideo.videoHeight) {
            return null;
        }

        const outW = 1080;
        const outH = 1920;

        const offscreen = document.createElement("canvas");
        offscreen.width = outW;
        offscreen.height = outH;

        const c = offscreen.getContext("2d");
        if (!c) return null;

        const srcW = cameraVideo.videoWidth;
        const srcH = cameraVideo.videoHeight;

        const srcAspect = srcW / srcH;
        const outAspect = outW / outH;

        let drawW, drawH, dx, dy;

        if (srcAspect > outAspect) {
            drawH = outH;
            drawW = srcW * (outH / srcH);
            dx = (outW - drawW) / 2;
            dy = 0;
        } else {
            drawW = outW;
            drawH = srcH * (outW / srcW);
            dx = 0;
            dy = (outH - drawH) / 2;
        }

        c.drawImage(cameraVideo, dx, dy, drawW, drawH);

        return offscreen.toDataURL("image/jpeg", 0.92);
    }

    async function startCameraWithSmoothTransition(sessionId) {
        const flowId = ++currentFlowId;

        if (heroActive) {
            return null;
        }

        heroActive = true;
        pendingHeroRequest = sessionId;

        const deferred = createDeferred();
        heroFinishedPromise = deferred.promise;
        heroFinishedResolve = deferred.resolve;

        try {
            ensureOverlayAndQR();
            resizeCanvas();

            await startCamera();
            await prepareLoopPair(false);
            await transitionIdleToHero();

            if (flowId !== currentFlowId) return null;

            monitorHeroForReturn(flowId).catch(() => { });

            showCountdown(COUNTDOWN_SECONDS);

            for (let i = COUNTDOWN_SECONDS; i >= 1; i--) {
                if (flowId !== currentFlowId) return null;
                updateCountdown(i);
                await sleep(1000);
            }

            hideCountdown();

            if (flowId !== currentFlowId) return null;

            const dataUrl = captureCurrentFrameDataUrl();
            if (!dataUrl) {
                throw new Error("Nije moguće captureati frame s kamere.");
            }

            return dataUrl;
        } catch (e) {
            console.error("startCameraWithSmoothTransition error:", e);
            await finalizeHeroToIdle();
            return null;
        }
    }

    async function waitForHeroFlowToFinish() {
        try {
            await heroFinishedPromise;
        } catch {
        }
    }

    window.drawOverlayOnly = drawOverlayOnly;
    window.startCameraWithSmoothTransition = startCameraWithSmoothTransition;
    window.waitForHeroFlowToFinish = waitForHeroFlowToFinish;

    ensureOverlayAndQR();
})();