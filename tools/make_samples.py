#!/usr/bin/env python3
"""Builds samples/payslip.rsr, samples/form16.rsr and their fixed sample data (*.data.json).

Run from the repository root:  python3 tools/make_samples.py
The .rsr files are Host reports (no query, no connection string); the data files are what a host would supply.
All names, numbers and IDs are made up.
"""
import json
from decimal import Decimal, ROUND_HALF_UP
from pathlib import Path

OUT = Path(__file__).resolve().parent.parent / "samples"
INK, BLUE, PALE, GREY = "#1F2937", "#1F3A5F", "#E6EEF7", "#555555"


def money(x):
    return float(Decimal(str(x)).quantize(Decimal("0.01"), rounding=ROUND_HALF_UP))


# ------------------------------------------------------------------ item helpers
def text(x, y, w, h, t, size=9, bold=False, italic=False, align="Left", valign="Middle", color=INK,
         back="", fmt="", grow=False):
    return {"Kind": "Text", "X": x, "Y": y, "Width": w, "Height": h, "Text": t, "FontSize": size,
            "Bold": bold, "Italic": italic, "HAlign": align, "VAlign": valign, "ForeColor": color,
            "BackColor": back, "Format": fmt, "CanGrow": grow, "WordWrap": True}


def line(x, y, w, color=GREY):
    return {"Kind": "Line", "X": x, "Y": y, "Width": w, "Height": 0, "BorderWidth": 1, "ForeColor": color}


def box(x, y, w, h, color=GREY):
    return {"Kind": "Rectangle", "X": x, "Y": y, "Width": w, "Height": h, "BorderWidth": 1, "BorderColor": color}


def band(kind, height, items=None):
    return {"Kind": kind, "Name": "", "Height": height, "Items": items or []}


def report(name, title, fields, params, group_expr, header_items, group_header, detail, footer_items,
           page_header_h, group_header_h, detail_h):
    """One group per employee: each starts a new page and restarts "Page n of m"."""
    return {
        "FormatVersion": 1, "Name": name, "Title": title,
        "Page": {"Width": 794, "Height": 1123, "MarginLeft": 40, "MarginRight": 40, "MarginTop": 40, "MarginBottom": 40},
        "DataSource": {"Provider": "Host", "ConnectionString": "", "Query": "", "CommandTimeoutSeconds": 60,
                       "Fields": [{"Name": n, "Type": t} for n, t in fields]},
        "Parameters": [{"Name": n, "Prompt": p, "Type": "Text", "DefaultValue": d} for n, p, d in params],
        "Groups": [{"Expression": group_expr, "StartNewPage": True, "RestartPageNumbers": True,
                    "Header": band("GroupHeader", group_header_h, group_header),
                    "Footer": band("GroupFooter", 0)}],
        "SortBy": [],
        "ReportHeader": band("ReportHeader", 0),
        "PageHeader": band("PageHeader", page_header_h, header_items),
        "Detail": band("Detail", detail_h, detail),
        "ReportFooter": band("ReportFooter", 0),
        "PageFooter": band("PageFooter", 24, footer_items),
    }


def save(name, definition, rows):
    OUT.mkdir(exist_ok=True)
    (OUT / f"{name}.rsr").write_text(json.dumps(definition, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    (OUT / f"{name}.data.json").write_text(json.dumps(rows, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


# ------------------------------------------------------------------ payslip
def payslip():
    # code, name, designation, department, joined, PAN, UAN, account, days, basic, hra, da, special, other,
    # pf_wage_cap, esi applies, professional tax, TDS, loan
    people = [
        ("E001", "Anita Sharma", "Senior Engineer", "Engineering", "2019-07-15", "ABCPS1234A", "100123456789",
         "1234567890123", 30, 85000, 34000, 8500, 42500, 0, None, False, 200, 9876.50, 5000),
        ("E002", "Rahul Verma", "Executive", "Accounts", "2022-04-01", "BCDPV2345B", "100987654321",
         "9876543210987", 28.5, 12000, 4800, 1200, 2000, 0, None, True, 200, 0, 0),
        ("E003", "Meera Iyer", "Director", "Management", "2015-01-10", "CDEPI3456C", "100555666777",
         "5556667778889", 30, 450000, 180000, 0, 270000.50, 100000, 15000, False, 200, 187654.25, 0),
    ]
    rows = []
    for (code, name, desig, dept, doj, pan, uan, acct, days, basic, hra, da, special, other,
         pf_cap, esi_applies, pt, tds, loan) in people:
        gross = money(basic + hra + da + special + other)
        pf_wage = basic + da if pf_cap is None else min(basic + da, pf_cap)
        pf = money(pf_wage * 0.12)
        esi = money(gross * 0.0075) if esi_applies else 0
        total = money(pf + esi + pt + tds + loan)
        rows.append({"EmpCode": code, "EmpName": name, "Designation": desig, "Department": dept, "JoinDate": doj,
                     "PAN": pan, "UAN": uan, "BankAccount": acct, "DaysPaid": days, "Basic": basic, "HRA": hra,
                     "DA": da, "SpecialAllowance": special, "OtherEarnings": other, "GrossEarnings": gross,
                     "PF": pf, "ESI": esi, "ProfessionalTax": pt, "TDS": tds, "LoanRecovery": loan,
                     "TotalDeductions": total, "NetPay": money(gross - total)})

    fields = [("EmpCode", "Text"), ("EmpName", "Text"), ("Designation", "Text"), ("Department", "Text"),
              ("JoinDate", "Date"), ("PAN", "Text"), ("UAN", "Text"), ("BankAccount", "Text"), ("DaysPaid", "Number"),
              ("Basic", "Number"), ("HRA", "Number"), ("DA", "Number"), ("SpecialAllowance", "Number"),
              ("OtherEarnings", "Number"), ("GrossEarnings", "Number"), ("PF", "Number"), ("ESI", "Number"),
              ("ProfessionalTax", "Number"), ("TDS", "Number"), ("LoanRecovery", "Number"),
              ("TotalDeductions", "Number"), ("NetPay", "Number")]
    params = [("CompanyName", "Company name", "Sample Industries Pvt Ltd"),
              ("CompanyAddress", "Company address", "12, Industrial Estate, Chennai 600032"),
              ("PayMonth", "Pay month", "October 2026")]

    header = [text(0, 4, 480, 26, "{@CompanyName}", size=16, bold=True, color=BLUE),
              text(0, 32, 480, 18, "{@CompanyAddress}", color=GREY),
              text(484, 4, 230, 26, "Payslip", size=14, bold=True, align="Right", color=BLUE),
              text(484, 32, 230, 18, "{@PayMonth}", align="Right", color=GREY),
              line(0, 58, 714, BLUE)]

    info = [box(0, 0, 714, 98)]
    left = [("Employee code", "{EmpCode}"), ("Name", "{EmpName}"), ("Designation", "{Designation}"),
            ("Department", "{Department}"), ("Date of joining", "{JoinDate}")]
    right = [("PAN", "{PAN}"), ("UAN", "{UAN}"), ("Bank account", "{BankAccount}"),
             ("Days paid", "{DaysPaid}"), ("Pay month", "{@PayMonth}")]
    for i, (lab, val) in enumerate(left):
        info += [text(8, 4 + i * 18, 100, 18, lab, color=GREY), text(110, 4 + i * 18, 240, 18, val, bold=True)]
    for i, (lab, val) in enumerate(right):
        fmt = "0.##" if lab == "Days paid" else ""
        info += [text(370, 4 + i * 18, 100, 18, lab, color=GREY), text(472, 4 + i * 18, 236, 18, val, bold=True, fmt=fmt)]

    d = [text(0, 0, 230, 24, "Earnings", bold=True, color="#FFFFFF", back=BLUE),
         text(230, 0, 127, 24, "Amount (Rs.)", bold=True, color="#FFFFFF", back=BLUE, align="Right"),
         text(357, 0, 230, 24, "Deductions", bold=True, color="#FFFFFF", back=BLUE),
         text(587, 0, 127, 24, "Amount (Rs.)", bold=True, color="#FFFFFF", back=BLUE, align="Right")]
    earn = [("Basic", "Basic"), ("House Rent Allowance", "HRA"), ("Dearness Allowance", "DA"),
            ("Special Allowance", "SpecialAllowance"), ("Other Earnings", "OtherEarnings")]
    ded = [("Provident Fund", "PF"), ("ESI", "ESI"), ("Professional Tax", "ProfessionalTax"),
           ("Income Tax (TDS)", "TDS"), ("Loan Recovery", "LoanRecovery")]
    for i, ((el, ef), (dl, df)) in enumerate(zip(earn, ded)):
        y = 28 + i * 24
        d += [text(0, y, 230, 22, el), text(230, y, 127, 22, "{" + ef + "}", fmt="N2", align="Right"),
              text(357, y, 230, 22, dl), text(587, y, 127, 22, "{" + df + "}", fmt="N2", align="Right")]
    d += [line(0, 150, 714),
          text(0, 154, 230, 22, "Gross earnings", bold=True),
          text(230, 154, 127, 22, "{GrossEarnings}", fmt="N2", bold=True, align="Right"),
          text(357, 154, 230, 22, "Total deductions", bold=True),
          text(587, 154, 127, 22, "{TotalDeductions}", fmt="N2", bold=True, align="Right"),
          text(0, 188, 357, 30, "Net pay", size=12, bold=True, back=PALE, color=BLUE),
          text(357, 188, 357, 30, "{NetPay}", size=12, bold=True, align="Right", back=PALE, color=BLUE, fmt="N2"),
          text(0, 226, 714, 20, "{AmountInWords(NetPay)}", italic=True, grow=True)]

    footer = [text(0, 2, 400, 18, "Computer generated payslip. No signature required.", size=8, color=GREY),
              text(514, 2, 200, 18, "Page {Page} of {PageCount}", size=8, align="Right", color=GREY)]

    definition = report("Payslip", "Employee payslip", fields, params, "EmpCode", header, info, d, footer, 64, 104, 256)
    save("payslip", definition, rows)


# ------------------------------------------------------------------ Form 16 style certificate
def form16():
    # code, name, PAN, gross salary, exempt allowances, Chapter VI-A deductions, tax on total income (sample figure)
    people = [
        ("E001", "Anita Sharma", "ABCPS1234A", 1620000.0, 240000.0, 150000.0, 52000.0),
        ("E002", "Rahul Verma", "BCDPV2345B", 240000.0, 0.0, 0.0, 0.0),
        ("E003", "Meera Iyer", "CDEPI3456C", 12600000.0, 0.0, 150000.0, 3480000.0),
    ]
    rows = []
    for (code, name, pan, gross, exempt, chap6a, tax) in people:
        std, pt = 75000.0, 2400.0
        income_salary = max(gross - exempt - std - pt, 0.0)
        taxable = max(income_salary - chap6a, 0.0)
        cess = money(tax * 0.04)
        total = money(tax + cess)
        rows.append({"EmpCode": code, "EmpName": name, "PAN": pan, "GrossSalary": gross, "ExemptAllowances": exempt,
                     "StandardDeduction": std, "ProfessionalTax": pt, "IncomeFromSalary": income_salary,
                     "ChapterVIADeductions": chap6a, "TaxableIncome": taxable, "TaxOnIncome": tax,
                     "Rebate": 0.0, "Cess": cess, "TotalTax": total, "TDSDeducted": total})

    fields = [("EmpCode", "Text"), ("EmpName", "Text"), ("PAN", "Text")] + [
        (n, "Number") for n in ["GrossSalary", "ExemptAllowances", "StandardDeduction", "ProfessionalTax",
                                "IncomeFromSalary", "ChapterVIADeductions", "TaxableIncome", "TaxOnIncome",
                                "Rebate", "Cess", "TotalTax", "TDSDeducted"]]
    params = [("EmployerName", "Employer name", "Sample Industries Pvt Ltd"),
              ("EmployerTAN", "Employer TAN", "CHEX12345A"),
              ("EmployerPAN", "Employer PAN", "AAACS1234F"),
              ("FinYear", "Financial year", "2025-26"),
              ("AssessmentYear", "Assessment year", "2026-27")]

    header = [text(0, 4, 714, 24, "Form 16 - Part B (sample layout)", size=15, bold=True, color=BLUE, align="Center"),
              text(0, 30, 714, 18, "Details of salary paid and tax deducted at source. Illustrative sample, not an official form.",
                   size=8, color=GREY, align="Center"),
              line(0, 54, 714, BLUE)]

    info = [box(0, 0, 714, 118),
            text(8, 4, 100, 18, "Employer", color=GREY), text(110, 4, 250, 18, "{@EmployerName}", bold=True),
            text(8, 22, 100, 18, "Employer TAN", color=GREY), text(110, 22, 250, 18, "{@EmployerTAN}", bold=True),
            text(8, 40, 100, 18, "Employer PAN", color=GREY), text(110, 40, 250, 18, "{@EmployerPAN}", bold=True),
            text(370, 4, 100, 18, "Employee", color=GREY), text(472, 4, 236, 18, "{EmpName}", bold=True),
            text(370, 22, 100, 18, "Employee code", color=GREY), text(472, 22, 236, 18, "{EmpCode}", bold=True),
            text(370, 40, 100, 18, "Employee PAN", color=GREY), text(472, 40, 236, 18, "{PAN}", bold=True),
            line(0, 64, 714),
            text(8, 70, 100, 18, "Financial year", color=GREY), text(110, 70, 250, 18, "{@FinYear}", bold=True),
            text(370, 70, 100, 18, "Assessment year", color=GREY), text(472, 70, 236, 18, "{@AssessmentYear}", bold=True),
            text(8, 92, 700, 18, "Period of employment: full year", color=GREY)]

    d = [text(0, 0, 520, 24, "Particulars", bold=True, color="#FFFFFF", back=BLUE),
         text(520, 0, 194, 24, "Amount (Rs.)", bold=True, color="#FFFFFF", back=BLUE, align="Right")]
    lines = [("1. Gross salary", "GrossSalary", False),
             ("2. Less: allowances exempt under section 10", "ExemptAllowances", False),
             ("3. Less: standard deduction", "StandardDeduction", False),
             ("4. Less: tax on employment (professional tax)", "ProfessionalTax", False),
             ("5. Income chargeable under the head Salaries", "IncomeFromSalary", True),
             ("6. Less: deductions under Chapter VI-A", "ChapterVIADeductions", False),
             ("7. Total taxable income", "TaxableIncome", True),
             ("8. Tax on total income", "TaxOnIncome", False),
             ("9. Less: rebate under section 87A", "Rebate", False),
             ("10. Health and education cess", "Cess", False),
             ("11. Total tax payable", "TotalTax", True),
             ("12. Tax deducted at source", "TDSDeducted", True)]
    for i, (label, field, strong) in enumerate(lines):
        y = 28 + i * 26
        if strong:
            d.append(text(0, y, 714, 24, "", back=PALE))
        d += [text(6, y, 514, 24, label, bold=strong),
              text(520, y, 188, 24, "{" + field + "}", fmt="N2", align="Right", bold=strong)]
    end = 28 + len(lines) * 26
    d += [line(0, end + 2, 714),
          text(0, end + 8, 714, 36, "Tax deducted in words: {AmountInWords(TDSDeducted)}", italic=True, grow=True),
          line(0, end + 64, 300),
          text(0, end + 68, 330, 18, "Signature of the person responsible for deduction", size=8, color=GREY)]

    footer = [text(0, 2, 400, 18, "{@EmployerName}  |  TAN {@EmployerTAN}", size=8, color=GREY),
              text(514, 2, 200, 18, "Page {Page} of {PageCount}", size=8, align="Right", color=GREY)]

    definition = report("Form16", "Form 16 style certificate", fields, params, "EmpCode", header, info, d, footer, 60, 124, 440)
    save("form16", definition, rows)


if __name__ == "__main__":
    payslip()
    form16()
    print("written to", OUT)
