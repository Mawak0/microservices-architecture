import { useEffect, useState } from 'react';

export function Photo({ blob, title }: { blob?: Blob; title: string }) {
  const [source, setSource] = useState<string>();
  const [angle, rotate] = useState(0);
  const [scale, zoom] = useState(1);
  useEffect(() => {
    setSource(undefined);
    if (!blob) return;
    const url = URL.createObjectURL(blob);
    setSource(url);
    return () => URL.revokeObjectURL(url);
  }, [blob]);
  if (!source) return <p role="status">Фото загружается или недоступно.</p>;
  return <div className="stack">
    <div className="photo-box"><img src={source} alt={title} style={{ transform: 'rotate(' + angle + 'deg) scale(' + scale + ')' }}/></div>
    <div className="row">
      <button className="quiet" onClick={() => rotate(value => (value + 90) % 360)}>Повернуть</button>
      <label>Масштаб<input type="range" min="1" max="3" step=".25" value={scale} onChange={e => zoom(Number(e.target.value))}/></label>
    </div>
  </div>;
}
