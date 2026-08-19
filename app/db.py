"""ตัวช่วยเชื่อมต่อ SQL Server ผ่าน pyodbc (Shared Memory / TCP ตาม ODBC)"""
import pyodbc
from contextlib import contextmanager
from typing import Any, Iterable
from .config import CONN_STR

pyodbc.pooling = True


@contextmanager
def conn():
    c = pyodbc.connect(CONN_STR, timeout=15)
    try:
        yield c
        c.commit()
    except Exception:
        c.rollback()
        raise
    finally:
        c.close()


def _rows(cur) -> list[dict]:
    cols = [d[0] for d in cur.description]
    return [dict(zip(cols, r)) for r in cur.fetchall()]


def query(sql: str, params: Iterable[Any] = ()) -> list[dict]:
    with conn() as c:
        cur = c.cursor()
        cur.execute(sql, *params) if params else cur.execute(sql)
        return _rows(cur)


def query_one(sql: str, params: Iterable[Any] = ()) -> dict | None:
    r = query(sql, params)
    return r[0] if r else None


def execute(sql: str, params: Iterable[Any] = ()) -> int:
    with conn() as c:
        cur = c.cursor()
        cur.execute(sql, *params) if params else cur.execute(sql)
        return cur.rowcount


def insert_returning_id(sql: str, params: Iterable[Any] = ()) -> int:
    """sql ต้องลงท้ายด้วย ; SELECT SCOPE_IDENTITY()"""
    with conn() as c:
        cur = c.cursor()
        cur.execute(sql, *params)
        while cur.description is None:
            if not cur.nextset():
                raise RuntimeError("no identity returned")
        return int(cur.fetchval())


def ping() -> dict:
    return query_one("SELECT DB_NAME() AS db, SUSER_NAME() AS usr, @@SERVERNAME AS srv") or {}
