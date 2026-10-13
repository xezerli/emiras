from app.statements.base import (IBAN_RE, Statement, Tx, cells, columns, date, find_header,
                                 label_value, num, register_parser, text)


def _tx_sheet(wb):
    return wb["account_statement"] if "account_statement" in wb.sheetnames else None


def _detect(wb):
    ws = _tx_sheet(wb)
    return ws is not None and find_header(cells(ws), "Transaction ID") is not None


@register_parser(_detect)
def parse(wb) -> Statement:
    summary = cells(wb["Summary"]) if "Summary" in wb.sheetnames else []
    grid = cells(_tx_sheet(wb))
    h = find_header(grid, "Transaction ID")
    col = columns(grid[h], date="Posting Date", ref="Transaction ID", cin="Mədaxil", out="Məxaric",
                  bal="Balans", name="Beneficiary Name", tin="TAX ID", purpose="Description")
    st = Statement(
        bank="Accessbank",
        iban=text(label_value(summary, "Account No")),
        currency=text(label_value(summary, "Currency")) or "AZN",
        opening=num(label_value(summary, "Starting Balance")),
        closing=num(label_value(summary, "Closing Balance")),
    )
    for r in grid[h + 1:]:
        d = date(r[col["date"]])
        if d is None:
            continue
        who = text(r[col["name"]]).split("\n") if r[col["name"]] else []
        acc = next((x for x in who if IBAN_RE.fullmatch(x.strip())), "")
        st.txs.append(Tx(
            date=d, ref=text(r[col["ref"]]),
            cp_tin=text(r[col["tin"]]), cp_name=text(who[0]) if who else "", cp_account=acc,
            credit=num(r[col["cin"]]), debit=num(r[col["out"]]),
            purpose=text(r[col["purpose"]]).replace("\n", " "), balance=num(r[col["bal"]])))
    return st
