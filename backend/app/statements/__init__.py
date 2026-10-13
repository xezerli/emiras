"""Bank statement (Excel) parsers. Each bank = one module that calls @register_parser."""
from app.statements.base import Statement, StatementError, detect_and_parse  # noqa: F401
from app.statements import abb, accessbank, aiib, respublika  # noqa: F401  (registers parsers)
