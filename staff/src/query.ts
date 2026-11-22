import { type DependencyList, useCallback, useEffect, useState } from 'react';

export function useQuery<T>(load: (signal: AbortSignal) => Promise<T>, dependencies: DependencyList) {
  const [data, setData] = useState<T>();
  const [error, setError] = useState<unknown>();
  const [refresh, setRefresh] = useState(0);
  const reload = useCallback(() => setRefresh(value => value + 1), []);

  useEffect(() => {
    const controller = new AbortController();
    load(controller.signal).then(value => {
      if (!controller.signal.aborted) {
        setData(value);
        setError(undefined);
      }
    }, error => {
      if (!controller.signal.aborted) setError(error);
    });
    return () => controller.abort();
  }, [...dependencies, refresh]);
  return { data, error, reload };
}

export function filterUrl(base: string, values: Record<string, string | number>) {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(values))
    if (value !== '') query.set(key, String(value));
  return base + '?' + query;
}
