import jsQR from 'jsqr';
import { ApiError } from '../api';

export interface Scan {
  qrToken: string;
  imageBase64: string;
  imageMediaType: 'image/jpeg' | 'image/png';
  url: string;
}
const maximum = 15 * 1024 * 1024;

export async function readScan(bytes: Blob, pixels = imagePixels): Promise<Scan> {
  const imageMediaType = bytes.type;
  if (imageMediaType !== 'image/jpeg' && imageMediaType !== 'image/png' || !bytes.size || bytes.size > maximum)
    throw new ApiError(422, 'INVALID_IMAGE');
  const image = await pixels(bytes);
  const qr = jsQR(image.data, image.width, image.height);
  if (!qr?.data) throw new ApiError(422, 'QR_UNREADABLE');
  const url = await dataUrl(bytes);
  return { qrToken: qr.data, imageBase64: url.split(',')[1], imageMediaType, url };
}

export function dataUrl(bytes: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result));
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(bytes);
  });
}

async function imagePixels(bytes: Blob): Promise<ImageData> {
  const bitmap = await createImageBitmap(bytes);
  try {
    if (bitmap.width * bitmap.height > 24_000_000) throw new ApiError(422, 'INVALID_IMAGE');
    const canvas = document.createElement('canvas');
    canvas.width = bitmap.width;
    canvas.height = bitmap.height;
    const context = canvas.getContext('2d')!;
    context.drawImage(bitmap, 0, 0);
    return context.getImageData(0, 0, bitmap.width, bitmap.height);
  } finally { bitmap.close(); }
}

export function frame(video: HTMLVideoElement): Promise<Blob> {
  const canvas = document.createElement('canvas');
  canvas.width = video.videoWidth;
  canvas.height = video.videoHeight;
  if (!canvas.width || !canvas.height) return Promise.reject(new ApiError(422, 'CAMERA_NOT_READY'));
  canvas.getContext('2d')!.drawImage(video, 0, 0);
  return new Promise((resolve, reject) => canvas.toBlob(blob => blob ? resolve(blob) : reject(new Error('Frame failed')), 'image/jpeg', .92));
}
