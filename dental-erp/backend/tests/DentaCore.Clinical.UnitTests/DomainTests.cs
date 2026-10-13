using DentaCore.BuildingBlocks.Domain;
using DentaCore.Clinical.Domain;

namespace DentaCore.Clinical.UnitTests;

public class FdiAndToothTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(11, true)]
    [InlineData(18, true)]
    [InlineData(48, true)]
    [InlineData(55, true)]
    [InlineData(85, true)]
    [InlineData(10, false)]
    [InlineData(19, false)]
    [InlineData(49, false)]
    [InlineData(56, false)]
    [InlineData(86, false)]
    [InlineData(0, false)]
    [InlineData(-11, false)]
    [InlineData(111, false)]
    public void Fdi_numbers_follow_the_standard(int tooth, bool valid) => Assert.Equal(valid, Fdi.IsValid(tooth));

    [Fact]
    public void Primary_teeth_are_quadrants_five_to_eight()
    {
        Assert.True(Fdi.IsPrimary(55));
        Assert.False(Fdi.IsPrimary(16));
    }

    [Fact]
    public void Tooth_record_is_validated()
    {
        Assert.True(ToothRecord.Create(Guid.NewGuid(), Guid.NewGuid(), null, 16, 'O', ToothCondition.Caries, null, null, Guid.NewGuid(), Now).IsSuccess);
        Assert.True(ToothRecord.Create(Guid.NewGuid(), Guid.NewGuid(), null, 16, null, ToothCondition.Extracted, null, null, Guid.NewGuid(), Now).IsSuccess);
        Assert.Equal("odontogram.invalid_tooth", ToothRecord.Create(Guid.NewGuid(), Guid.NewGuid(), null, 19, null, ToothCondition.Caries, null, null, Guid.NewGuid(), Now).Error!.Code);
        Assert.Equal("odontogram.invalid_surface", ToothRecord.Create(Guid.NewGuid(), Guid.NewGuid(), null, 16, 'Z', ToothCondition.Caries, null, null, Guid.NewGuid(), Now).Error!.Code);
        Assert.Equal("odontogram.text_too_long", ToothRecord.Create(Guid.NewGuid(), Guid.NewGuid(), null, 16, null, ToothCondition.Caries, null, new string('x', 501), Guid.NewGuid(), Now).Error!.Code);
    }
}

public class VisitTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Start_and_close_raise_events_and_closing_twice_is_a_conflict()
    {
        var visit = Visit.Start(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "  ağrı ", Now).Value;
        Assert.Equal("ağrı", visit.ChiefComplaint);
        Assert.IsType<VisitStarted>(Assert.Single(visit.DomainEvents));
        visit.ClearDomainEvents();

        Assert.True(visit.Close(Now.AddMinutes(40)).IsSuccess);

        var closed = Assert.IsType<VisitClosed>(Assert.Single(visit.DomainEvents));
        Assert.Equal(Now, closed.StartedAt);
        Assert.Equal(Now.AddMinutes(40), closed.EndedAt);
        Assert.Equal("visit.already_closed", visit.Close(Now).Error!.Code);
    }

    [Fact]
    public void Complaint_length_is_limited() =>
        Assert.Equal("visit.complaint_too_long", Visit.Start(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, new string('x', 1001), Now).Error!.Code);
}

public class TreatmentPlanTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Patient = Guid.NewGuid();
    private static readonly Guid Doctor = Guid.NewGuid();
    private static readonly Guid Visit = Guid.NewGuid();

    private static NewPlanItem Item(string code = "D2391", int? tooth = 16, decimal price = 100m, int qty = 1, decimal discount = 0, int phase = 1) =>
        new(code, tooth, null, phase, qty, price, discount);

    private static TreatmentPlan Plan(params NewPlanItem[] items) =>
        TreatmentPlan.Create(Guid.NewGuid(), Patient, Doctor, "Plan", items.Length == 0 ? [Item(), Item("D1110", null, 250m)] : items, Now).Value;

    private static TreatmentPlan Accepted(params NewPlanItem[] items)
    {
        var plan = Plan(items);
        plan.Propose();
        plan.Accept(Now);
        plan.ClearDomainEvents();
        return plan;
    }

    [Fact]
    public void Totals_use_quantity_discount_and_bankers_rounding()
    {
        var plan = Plan(Item(price: 100m, qty: 2, discount: 10m), Item("D1110", null, 250m), Item(price: 33.33m, qty: 3, discount: 10m));

        Assert.Equal([180.00m, 250.00m, 89.99m], plan.Items.Select(i => i.LineTotal));   // 3 × 33.33 × 0.9 = 89.991
        Assert.Equal(519.99m, plan.Total);
        Assert.Equal(PlanStatus.Draft, plan.Status);
        Assert.Equal([0, 1, 2], plan.Items.Select(i => i.SortOrder));
    }

    [Theory]
    [InlineData("", 1, "plan.invalid_title")]
    [InlineData("T", 0, "plan.invalid_items")]
    [InlineData("T", 51, "plan.invalid_items")]
    public void Plan_header_and_item_count_are_validated(string title, int count, string code) =>
        Assert.Equal(code, TreatmentPlan.Create(Guid.NewGuid(), Patient, Doctor, title, Enumerable.Repeat(Item(), count).ToList(), Now).Error!.Code);

    [Theory]
    [InlineData(-1, 0, 1, 1, "plan.invalid_price")]
    [InlineData(10, 101, 1, 1, "plan.invalid_price")]
    [InlineData(10, -1, 1, 1, "plan.invalid_price")]
    [InlineData(10, 0, 0, 1, "plan.invalid_item")]
    [InlineData(10, 0, 100, 1, "plan.invalid_item")]
    [InlineData(10, 0, 1, 0, "plan.invalid_item")]
    [InlineData(10, 0, 1, 21, "plan.invalid_item")]
    public void Item_values_are_validated(double price, double discount, int qty, int phase, string code) =>
        Assert.Equal(code, TreatmentPlan.Create(Guid.NewGuid(), Patient, Doctor, "T", [Item(price: (decimal)price, discount: (decimal)discount, qty: qty, phase: phase)], Now).Error!.Code);

    [Fact]
    public void Invalid_tooth_or_surface_is_rejected()
    {
        Assert.Equal("plan.invalid_tooth", TreatmentPlan.Create(Guid.NewGuid(), Patient, Doctor, "T", [Item(tooth: 99)], Now).Error!.Code);
        Assert.Equal("plan.invalid_surface", TreatmentPlan.Create(Guid.NewGuid(), Patient, Doctor, "T", [Item() with { Surface = 'X' }], Now).Error!.Code);
    }

    [Fact]
    public void Status_machine_allows_only_the_documented_transitions()
    {
        var plan = Plan();

        Assert.Equal("plan.invalid_state", plan.Accept(Now).Error!.Code);     // əvvəl təklif
        Assert.Equal("plan.invalid_state", plan.Reject().Error!.Code);
        Assert.True(plan.Propose().IsSuccess);
        Assert.Equal("plan.invalid_state", plan.Propose().Error!.Code);
        Assert.True(plan.Accept(Now).IsSuccess);
        Assert.Equal(Now, plan.AcceptedAt);
        Assert.IsType<TreatmentPlanAccepted>(Assert.Single(plan.DomainEvents));
        Assert.Equal(350m, ((TreatmentPlanAccepted)plan.DomainEvents.Single()).Total);   // 100 + 250
        Assert.Equal("plan.invalid_state", plan.Reject().Error!.Code);
    }

    [Fact]
    public void Rejected_plan_is_final()
    {
        var plan = Plan();
        plan.Propose();

        Assert.True(plan.Reject().IsSuccess);

        Assert.Equal("plan.invalid_state", plan.Cancel().Error!.Code);
        Assert.Equal("plan.not_accepted", plan.PerformItem(plan.Items[0].Id, Visit, Doctor, Now).Error!.Code);
    }

    [Fact]
    public void Performing_items_raises_billing_events_with_frozen_prices_and_completes_the_plan()
    {
        var plan = Accepted(Item(price: 100m, qty: 2, discount: 10m), Item("D1110", null, 250m));

        var first = plan.PerformItem(plan.Items[0].Id, Visit, Doctor, Now);

        Assert.True(first.IsSuccess);
        Assert.Equal(PlanStatus.InProgress, plan.Status);
        var e = Assert.IsType<ProcedurePerformed>(Assert.Single(plan.DomainEvents));
        Assert.Equal((plan.Items[0].Id, "D2391", 16, 2, 100m, 10m), (e.PlanItemId, e.ProcedureCode, e.ToothFdi, e.Quantity, e.UnitPrice, e.DiscountPercent));
        Assert.Equal(Visit, e.VisitId);

        plan.ClearDomainEvents();
        Assert.True(plan.PerformItem(plan.Items[1].Id, Visit, Doctor, Now).IsSuccess);
        Assert.Equal(PlanStatus.Completed, plan.Status);
        Assert.Equal(2, plan.DomainEvents.Count);
        Assert.IsType<TreatmentPlanCompleted>(plan.DomainEvents.Last());
    }

    [Fact]
    public void An_item_can_be_performed_only_once_and_must_exist()
    {
        var plan = Accepted();
        var id = plan.Items[0].Id;
        plan.PerformItem(id, Visit, Doctor, Now);

        Assert.Equal("plan.item_not_pending", plan.PerformItem(id, Visit, Doctor, Now).Error!.Code);
        Assert.Equal("plan.item_not_found", plan.PerformItem(Guid.NewGuid(), Visit, Doctor, Now).Error!.Code);
    }

    [Fact]
    public void Cancelling_keeps_performed_items_cancels_the_rest_and_excludes_them_from_the_total()
    {
        var plan = Accepted(Item(price: 100m), Item(price: 200m), Item(price: 300m));
        plan.PerformItem(plan.Items[0].Id, Visit, Doctor, Now);

        Assert.True(plan.Cancel().IsSuccess);

        Assert.Equal([ItemStatus.Done, ItemStatus.Cancelled, ItemStatus.Cancelled], plan.Items.Select(i => i.Status));
        Assert.Equal(100m, plan.Total);
        Assert.Equal("plan.invalid_state", plan.Cancel().Error!.Code);
        Assert.Equal("plan.not_accepted", plan.PerformItem(plan.Items[1].Id, Visit, Doctor, Now).Error!.Code);
    }
}

public class PrescriptionAndAllergyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    private static PrescriptionItem Rx(string drug = "Paracetamol", string dose = "500 mq", int? days = 3) => new(drug, dose, "3x1", days, null);

    private static Result<Prescription> Issue(IReadOnlyList<PrescriptionItem> items, bool conflict = false, string? reason = null) =>
        Prescription.Issue(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), items, conflict, reason, Now);

    [Fact]
    public void Valid_prescription_passes_the_allergy_flag_and_raises_event()
    {
        var p = Issue([Rx(" Paracetamol ", " 500 mq ")]).Value;

        Assert.True(p.AllergyCheckPassed);
        Assert.Null(p.OverrideReason);
        Assert.Equal("Paracetamol", p.Items[0].Drug);
        Assert.False(Assert.IsType<PrescriptionIssued>(Assert.Single(p.DomainEvents)).AllergyOverride);
    }

    [Theory]
    [InlineData("", "500 mq", 3)]
    [InlineData("Drug", "", 3)]
    [InlineData("Drug", "500 mq", 0)]
    [InlineData("Drug", "500 mq", 366)]
    public void Items_are_validated(string drug, string dose, int days) =>
        Assert.Equal("prescription.invalid_item", Issue([Rx(drug, dose, days)]).Error!.Code);

    [Fact]
    public void Item_count_is_limited()
    {
        Assert.Equal("prescription.invalid_items", Issue([]).Error!.Code);
        Assert.Equal("prescription.invalid_items", Issue(Enumerable.Repeat(Rx(), 21).ToList()).Error!.Code);
    }

    [Fact]
    public void Overriding_a_conflict_requires_a_real_reason()
    {
        Assert.Equal("prescription.override_reason_required", Issue([Rx()], true, null).Error!.Code);
        Assert.Equal("prescription.override_reason_required", Issue([Rx()], true, "qısa").Error!.Code);

        var ok = Issue([Rx()], true, "Alternativ yoxdur, pasiyent xəbərdar edilib").Value;

        Assert.False(ok.AllergyCheckPassed);
        Assert.StartsWith("Alternativ", ok.OverrideReason, StringComparison.Ordinal);
        Assert.True(((PrescriptionIssued)ok.DomainEvents.Single()).AllergyOverride);
    }

    [Theory]
    [InlineData("Amoksisillin 500 mq", "Penisillin", true)]
    [InlineData("Amoxicillin", "penicillin", true)]
    [InlineData("Augmentin 1000", "Penisillin", true)]          // sinif üzvü
    [InlineData("Ibuprofen 400", "NSAID", true)]
    [InlineData("Aspirin", "ibuprofen", true)]                  // eyni sinif
    [InlineData("Lidokain 2%", "Lidocaine", true)]              // yazılış fərqi, eyni sinif
    [InlineData("Ultracain", "Anestezik", true)]
    [InlineData("Lateks əlcək", "Latex", true)]
    [InlineData("AMOKSİSİLLİN", "penisillin", true)]            // böyük hərf və diakritik
    [InlineData("Paracetamol", "Penisillin", false)]
    [InlineData("Metronidazol", "Penisillin", false)]
    [InlineData("Amoksisillin", "Lateks", false)]
    [InlineData("Amoksisillin", "A", false)]                    // çox qısa allergen təsadüfi uyğunluq verməsin
    [InlineData("Azitromisin", "Penisillin", false)]
    public void Allergy_checker_matches_names_and_drug_classes(string drug, string allergen, bool conflict) =>
        Assert.Equal(conflict, AllergyChecker.Check([drug], [(allergen, "severe")]).Count > 0);

    [Fact]
    public void Conflicts_report_each_drug_allergen_pair_with_severity()
    {
        var conflicts = AllergyChecker.Check(["Amoksisillin", "Paracetamol", "Ibuprofen"], [("Penisillin", "severe"), ("Aspirin", "mild")]);

        Assert.Equal([("Amoksisillin", "Penisillin", "severe"), ("Ibuprofen", "Aspirin", "mild")], conflicts.Select(c => (c.Drug, c.Allergen, c.Severity)));
    }
}

public class ClinicalNoteTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    private static ClinicalNote Note(string? s = "Ağrı", string source = "typed", Guid? addendumOf = null) =>
        ClinicalNote.Create(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), new SoapText(s, null, null, null), source, addendumOf, Now).Value;

    [Fact]
    public void Draft_can_be_edited_then_signed_and_becomes_immutable()
    {
        var note = Note();

        Assert.True(note.Edit(new SoapText("Yeni", "Obyektiv", null, null)).IsSuccess);
        Assert.Equal("Obyektiv", note.Objective);
        Assert.True(note.Sign(Now).IsSuccess);
        Assert.IsType<ClinicalNoteSigned>(Assert.Single(note.DomainEvents));

        Assert.Equal("clinical.note_signed", note.Edit(new SoapText("hack", null, null, null)).Error!.Code);
        Assert.Equal("clinical.note_signed", note.Sign(Now).Error!.Code);
        Assert.Equal("Yeni", note.Subjective);
    }

    [Fact]
    public void Content_rules_apply_on_create_and_edit()
    {
        Assert.Equal("note.empty", ClinicalNote.Create(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), new SoapText(" ", null, "", null), "typed", null, Now).Error!.Code);
        Assert.Equal("note.too_long", ClinicalNote.Create(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), new SoapText(new string('x', 5001), null, null, null), "typed", null, Now).Error!.Code);
        Assert.Equal("note.invalid_source", ClinicalNote.Create(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), new SoapText("x", null, null, null), "fax", null, Now).Error!.Code);
        Assert.Equal("note.empty", Note().Edit(new SoapText(null, null, null, null)).Error!.Code);
    }

    [Fact]
    public void Addendum_references_the_original()
    {
        var original = Guid.NewGuid();

        Assert.Equal(original, Note(addendumOf: original).AddendumOf);
    }
}
