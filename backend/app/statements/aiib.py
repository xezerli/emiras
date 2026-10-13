from app.statements.base import (Statement, Tx, cells, columns, date, find_header, label_value,
                                 num, register_parser, text)


def _detect(wb):
    return find_header(cells(wb.active), "Sənədin nömrəsi", "AZN dəyəri") is not None


@register_parser(_detect)
def parse(wb) -> Statement:
    grid = cells(wb.active)
    h = find_header(grid, "Sənədin nömrəsi", "AZN dəyəri")
    col = columns(grid[h], date="Tarix", ref="Sənədin nömrəsi", cp="Əks hesab",
                  amount="Məbləğ", bal="Balans", purpose="Təyinat")
    st = Statement(
        bank="AIIB",
        iban=text(label_value(grid, "Account number", h)),
        currency=text(label_value(grid, "Account currency", h)) or "AZN",
        owner_tin=text(label_value(grid, "Tax id", h)),
        opening=num(label_value(grid, "İlkin qalıq", h)),
        closing=num(label_value(grid, "Son qalıq", h)),
    )
    for r in grid[h + 1:]:
        d = date(r[col["date"]])
        if d is None:
            continue
        # "IBAN / name / VÖEN" in one cell; the name itself may contain slashes
        parts = [p.strip() for p in text(r[col["cp"]]).split(" / ")]
        acc = parts[0] if parts else ""
        tin = parts[-1] if len(parts) >= 3 and parts[-1].isdigit() else ""
        name = " / ".join(parts[1:-1] if tin else parts[1:])
        amt = num(r[col["amount"]])
        st.txs.append(Tx(
            date=d, ref=text(r[col["ref"]]), cp_tin=tin, cp_name=name, cp_account=acc,
            credit=max(amt, 0.0), debit=max(-amt, 0.0),
            purpose=text(r[col["purpose"]]), balance=num(r[col["bal"]])))
    return st
