"use strict";

const maxQrImageBytes = 32 * 1024 * 1024;
const maxQrImagePixels = 24_000_000;
const qrByteCount = text => new TextEncoder().encode(text).length;
const canGenerateQr = text => !!text && text.length <= 2000 && qrByteCount(text) <= 2000;

function qrScanActions() {
  return '<div class="toolbar">'+utilityButton("qr-read-image","识别图片…","secondary")+
    '<input type="file" id="qr-image-file" accept="image/png,image/jpeg,image/webp,image/bmp" aria-label="二维码图片" hidden>'+
    '<button type="button" id="qr-camera" hidden>摄像头…</button></div>';
}

function connectQrScanner(generator, applyText) {
  const read = el("qr-read-image"), picker = el("qr-image-file"), cameraButton = el("qr-camera");
  const dialog = el("dialog");
  let worker, pending, camera;
  cameraButton.hidden = !navigator.mediaDevices?.getUserMedia;

  function decode(image, width, height, maximum) {
    const ratio = Math.min(1,maximum/Math.max(width,height));
    const surface = document.createElement("canvas");
    surface.width = Math.max(1,Math.round(width*ratio));
    surface.height = Math.max(1,Math.round(height*ratio));
    const ctx = surface.getContext("2d",{alpha:false,willReadFrequently:true});
    ctx.fillStyle = "#fff";
    ctx.fillRect(0,0,surface.width,surface.height);
    ctx.drawImage(image,0,0,surface.width,surface.height);
    const data = ctx.getImageData(0,0,surface.width,surface.height);
    worker ??= new Worker("/qr-decode-worker.js");
    return new Promise((resolve,reject)=>{
      pending = reject;
      worker.onmessage = ({data:message}) => {
        pending = null;
        if (message.error) reject(new Error("无法识别图片。"));
        else resolve(message.text);
      };
      worker.onerror = () => {
        pending = null;
        reject(new Error("无法启动二维码识别。"));
      };
      worker.postMessage({width:data.width,height:data.height,pixels:data.data.buffer},[data.data.buffer]);
    });
  }

  function stopDecoder() {
    worker?.terminate();
    worker = undefined;
    pending?.(new DOMException("Cancelled","AbortError"));
    pending = null;
  }

  function showResult(text) {
    setUtilityStatus("");
    const use = canGenerateQr(text) ? utilityButton("qr-scan-use","用作生成内容") : "";
    showDialog("二维码识别结果",'<textarea id="qr-scan-result" aria-label="识别结果" readonly spellcheck="false"></textarea>'+
      '<div class="actions qr-scan-actions">'+button("关闭","close-dialog")+use+
      utilityButton("qr-scan-copy","复制结果","primary")+'</div>',"qr-result");
    const content=el("qr-scan-result");
    content.value=text;
    el("qr-scan-copy").addEventListener("click",()=>copyText(text));
    el("qr-scan-use")?.addEventListener("click",()=>{
      applyText(text);
      dialog.close();
      generator.focus({preventScroll:true});
    });
  }

  read.addEventListener("click",()=>picker.click());
  picker.addEventListener("change",async ()=>{
    const file=picker.files[0];
    picker.value="";
    if (!file) return;
    if (file.size > maxQrImageBytes) { setUtilityStatus("图片超过 32 MiB 或 2,400 万像素。","error"); return; }
    setUtilityStatus("正在识别二维码…");
    read.disabled = cameraButton.disabled = true;
    let picture;
    try {
      try { picture=await createImageBitmap(file); }
      catch { throw new Error("无法读取图片。"); }
      if (!generator.isConnected) return;
      if (picture.width * picture.height > maxQrImagePixels) throw new Error("图片超过 32 MiB 或 2,400 万像素。");
      const text=await decode(picture,picture.width,picture.height,2560);
      if (!generator.isConnected) return;
      if (text) showResult(text);
      else setUtilityStatus("图片中未找到二维码。","error");
    } catch (error) {
      if (generator.isConnected && error.name!=="AbortError") setUtilityStatus(error.message,"error");
    } finally {
      picture?.close();
      stopDecoder();
      read.disabled = cameraButton.disabled = false;
    }
  });

  function stopCamera() {
    if (!camera) return;
    const session=camera;
    camera=undefined;
    clearTimeout(session.timer);
    session.stream?.getTracks().forEach(track=>track.stop());
    session.video.srcObject=null;
    dialog.removeEventListener("close",stopCamera);
    stopDecoder();
  }

  function visibilityChanged() {
    if (!document.hidden || !camera) return;
    stopCamera();
    if (dialog.dataset.mode==="qr-camera") dialog.close();
  }

  cameraButton.addEventListener("click",async ()=>{
    showDialog("摄像头识别",'<video id="qr-video" aria-label="摄像头预览" muted playsinline hidden></video>'+
      '<p id="qr-camera-status" role="status">等待摄像头权限…</p><div class="actions qr-scan-actions">'+
      button("关闭","close-dialog")+'</div>',"qr-camera");
    const video=el("qr-video"), status=el("qr-camera-status");
    const session=camera={video};
    dialog.addEventListener("close",stopCamera,{once:true});
    try {
      const stream=await navigator.mediaDevices.getUserMedia({
        audio:false,video:{facingMode:{ideal:"environment"},width:{ideal:1280},height:{ideal:720}}
      });
      if (camera!==session || !dialog.open || !video.isConnected) {
        stream.getTracks().forEach(track=>track.stop());
        return;
      }
      session.stream=stream;
      video.srcObject=stream;
      video.hidden=false;
      stream.getVideoTracks().forEach(track=>track.addEventListener("ended",()=>{
        if(camera!==session) return;
        stopCamera();
        video.hidden=true;
        status.textContent="摄像头已关闭。";
      },{once:true}));
      await video.play();
      if (camera!==session) return;
      status.textContent="等待识别…";
      await sample();
    } catch {
      if(camera!==session) return;
      stopCamera();
      video.hidden=true;
      status.textContent="摄像头未授权或不可用，请选择图片识别。";
    }

    async function sample() {
      if (camera!==session || !dialog.open || !video.isConnected) return;
      try {
        if (video.readyState>=2 && video.videoWidth && video.videoHeight) {
          const text=await decode(video,video.videoWidth,video.videoHeight,960);
          if (camera!==session || !dialog.open) return;
          if (text) { stopCamera(); showResult(text); return; }
        }
        session.timer=setTimeout(sample,200);
      } catch (error) {
        if (camera!==session || error.name==="AbortError") return;
        stopCamera();
        video.hidden=true;
        status.textContent=error.message;
      }
    }
  });

  document.addEventListener("visibilitychange",visibilityChanged);
  return ()=>{
    document.removeEventListener("visibilitychange",visibilityChanged);
    stopCamera();
    stopDecoder();
    if(dialog.open && ["qr-camera","qr-result"].includes(dialog.dataset.mode)) dialog.close();
  };
}
