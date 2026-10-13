import importlib
import pkgutil
from app.connectors.base import BankConnector

_banks: dict[str, BankConnector] = {}


def register_bank(cls):
    inst = cls()
    _banks[inst.slug] = inst
    return cls


def load_all() -> None:
    from app.connectors import banks
    for m in pkgutil.iter_modules(banks.__path__):
        importlib.import_module(f"{banks.__name__}.{m.name}")


def all_banks() -> list[BankConnector]:
    return sorted(_banks.values(), key=lambda b: b.name)


def get_bank(slug: str) -> BankConnector | None:
    return _banks.get(slug)
