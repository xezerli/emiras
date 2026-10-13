from app.statements.base import (Statement, Tx, cells, columns, date, find_header, label_value,
                                 num, register_parser, text)


def _detect(wb):
    return find_header(cells(wb.active), "Göndərənin adı/Alanın adı", "Kredit", "Debet") is not None


@register_parser(_detect)
def parse(wb) -> Statement:
    grid = cells(wb.active)
    h = find_header(grid, "Göndərənin adı/Alanın adı", "Kredit", "Debet")
    col = columns(grid[h], date="Tarix", ref="Sənədin N", name="Göndərənin adı", tin="VÖEN",
                  acc="Hesab", purpose="Ödənişin təyinatı", credit="Kredit", debit="Debet",
                  bal="Qalıq")
    st = Statement(
        bank="Bank Respublika",
        iban=text(label_value(grid, "Hesabın nömrəsi", h)),
        currency=text(label_value(grid, "Valyutanın kodu", h)) or "AZN",
        owner_tin=text(label_value(grid, "VÖEN", h)),
        opening=num(label_value(grid, "Giriş qalığı", h)),
        closing=num(label_value(grid, "Çıxış qalığı", h)),
    )
    for r in grid[h + 1:]:
        d = date(r[col["date"]])
        if d is None:
            continue
        st.txs.append(Tx(
            date=d, ref=text(r[col["ref"]]), cp_tin=text(r[col["tin"]]),
            cp_name=text(r[col["name"]]), cp_account=text(r[col["acc"]]),
            credit=num(r[col["credit"]]), debit=num(r[col["debit"]]),
            purpose=text(r[col["purpose"]]), balance=num(r[col["bal"]])))
    return st
