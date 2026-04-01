const apiBase = "https://booth-api-d0el.onrender.com";

window.startCameraWithCountdown = async (sessionId) => {
    const video = document.getElementById('video');
    const countdown = document.getElementById('countdown');
    const canvas = document.getElementById('canvas');

    const stream = await navigator.mediaDevices.getUserMedia({ video: true });
    video.srcObject = stream;

    let count = 3;
    countdown.innerText = count;

    const interval = setInterval(() => {
        count--;
        countdown.innerText = count > 0 ? count : "📸";
        if (count <= 0) {
            clearInterval(interval);
            setTimeout(() => takePhoto(sessionId), 500);
        }
    }, 1000);
};

function takePhoto(sessionId) {
    const video = document.getElementById('video');
    const canvas = document.getElementById('canvas');
    canvas.width = 1080;
    canvas.height = 1920;
    const ctx = canvas.getContext('2d');

    const overlay = new Image();
    overlay.src = '/overlay.png';

    overlay.onload = async () => {
        // 🖼️ Draw overlay background
        ctx.drawImage(overlay, 0, 0, canvas.width, canvas.height);

        // 📸 Draw camera feed inside the target square
        const photoX = 150;
        const photoY = 500;
        const photoWidth = 800;
        const photoHeight = 800;

        ctx.drawImage(video, photoX, photoY, photoWidth, photoHeight);

        // 📤 Upload photo to backend
        canvas.toBlob(async (blob) => {
            const formData = new FormData();
            formData.append("photo", blob, "photo.jpg");

            try {
                const response = await fetch(`${apiBase}/api/session/${sessionId}/photo`, {
                    method: "POST",
                    body: formData
                });

                if (!response.ok) {
                    const errorText = await response.text();
                    console.error("Upload error:", errorText);
                } else {
                    console.log("Photo uploaded successfully.");

                    // ✅ Pozovi Blazor metodu za zahvalu i reset
                    DotNet.invokeMethodAsync("Booth", "ShowThankYouAndReset");
                }
            } catch (err) {
                console.error("Fetch exception:", err);
            }
        }, "image/jpeg");
    };
}
