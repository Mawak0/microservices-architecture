import { afterEach, expect, it, vi } from 'vitest';
import jsQR from 'jsqr';
import { frame, readScan } from '../src/observer/camera';

vi.mock('jsqr', () => ({ default: vi.fn() }));
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it('decodes the same bytes that will be sent and rejects invalid media or QR', async () => {
  const bytes = new Blob([Uint8Array.from([1, 2, 3])], { type: 'image/png' });
  const pixels = { data: new Uint8ClampedArray(4), width: 1, height: 1 } as ImageData;
  const close = vi.fn();
  vi.stubGlobal('createImageBitmap', vi.fn(async () => ({ width: 1, height: 1, close })));
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue({
    drawImage: vi.fn(), getImageData: () => pixels,
  } as unknown as CanvasRenderingContext2D);
  vi.mocked(jsQR).mockReturnValueOnce({ data: 'qr-123' } as ReturnType<typeof jsQR>);
  expect(await readScan(bytes)).toMatchObject({ qrToken: 'qr-123', imageBase64: 'AQID', imageMediaType: 'image/png' });
  expect(close).toHaveBeenCalled();
  await expect(readScan(bytes)).rejects.toMatchObject({ code: 'QR_UNREADABLE' });
  await expect(readScan(new Blob(['other']))).rejects.toMatchObject({ code: 'INVALID_IMAGE' });
});

it('waits for a camera frame and renders the final JPEG', async () => {
  const video = document.createElement('video');
  await expect(frame(video)).rejects.toMatchObject({ code: 'CAMERA_NOT_READY' });
  Object.defineProperty(video, 'videoWidth', { value: 640 });
  Object.defineProperty(video, 'videoHeight', { value: 480 });
  const drawImage = vi.fn();
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue({ drawImage } as unknown as CanvasRenderingContext2D);
  vi.spyOn(HTMLCanvasElement.prototype, 'toBlob').mockImplementation((callback, type) =>
    callback(new Blob(['pixels'], { type })));
  expect((await frame(video)).type).toBe('image/jpeg');
  expect(drawImage).toHaveBeenCalledWith(video, 0, 0);
});
