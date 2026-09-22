import { useEffect, useRef, useState } from 'react';

export interface PagedResponse<T> {
  results: T[];
  total: number;
}

/* Reusable server-side paging. Owns page / pageSize / rows / total and re-fetches the CURRENT
   page whenever the page, the page size, a manual reload, or any of `deps` (the caller's filters)
   changes — only one page is ever loaded, so search / date / filter must be applied server-side by
   `fetcher`. A change in `deps` resets back to page 1. Any extra fields the endpoint returns beyond
   { results, total } (e.g. per-tab counts) are exposed via `data`.

   `fetcher` may be a fresh closure each render (it is read through a ref, so its identity is NOT a
   refetch trigger) — list the actual filter values in `deps` instead. Return a falsy value (e.g.
   from a guard() that swallowed an error) and the list is shown empty rather than throwing. */
export function usePagedList<T, R extends PagedResponse<T> = PagedResponse<T>>(
  fetcher: (page: number, pageSize: number) => Promise<R | null | undefined>,
  deps: readonly unknown[],
  initialPageSize = 10,
) {
  const [rows, setRows] = useState<T[] | null>(null);
  const [total, setTotal] = useState(0);
  const [data, setData] = useState<R | null>(null);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(initialPageSize);
  const [reloadKey, setReloadKey] = useState(0);

  const fetcherRef = useRef(fetcher);
  fetcherRef.current = fetcher;
  const depsKey = JSON.stringify(deps);

  // Filters changed → back to the first page.
  useEffect(() => {
    setPage(1);
  }, [depsKey]);

  useEffect(() => {
    let cancelled = false;
    setRows(null);
    Promise.resolve(fetcherRef.current(page, pageSize)).then((r) => {
      if (cancelled) return;
      if (!r) {
        setRows([]);
        setTotal(0);
        setData(null);
        return;
      }
      setRows(r.results ?? []);
      setTotal(r.total ?? 0);
      setData(r);
    });
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [depsKey, page, pageSize, reloadKey]);

  return {
    rows,
    total,
    data,
    page,
    pageSize,
    setPage,
    setPageSize,
    reload: () => setReloadKey((k) => k + 1),
  };
}
