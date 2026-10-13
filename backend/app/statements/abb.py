from app.statements.base import (Statement, Tx, cells, columns, date, find_header, label_value,
                                 num, register_parser, split_counterparty, text)


def _detect(wb):
    return find_header(cells(wb.active), "Əməliyyatın referensi", "Debit məbləğ") is not None


@register_parser(_detect)
def parse(wb) -> Statement:
    grid = cells(wb.active)
    h = find_header(grid, "Əməliyyatın referensi", "Debit məbləğ")
    col = columns(grid[h], date="operation date", ref="reference", cp="counterparty account",
                  debit="debit amount", credit="credit amount", purpose="description",
                  tin="counterparty tax")
    st = Statement(
        bank="ABB",
        iban=text(label_value(grid, "IBAN / IBAN", h)),
        currency=text(label_value(grid, "account currency", h)) or "AZN",
        owner_tin=text(label_value(grid, "TAX ID:", h)),
        opening=num(label_value(grid, "Starting balance", h)),
        closing=num(label_value(grid, "Yekun balans", h)),
    )
    for r in grid[h + 1:]:
        d = date(r[col["date"]])
        if d is None:
            continue
        acc, name, tin = split_counterparty(text(r[col["cp"]]))
        st.txs.append(Tx(
            date=d, ref=text(r[col["ref"]]),
            cp_tin=text(r[col["tin"]]) or tin, cp_name=name, cp_account=acc,
            debit=num(r[col["debit"]]), credit=num(r[col["credit"]]),
            purpose=text(r[col["purpose"]])))
    return st
