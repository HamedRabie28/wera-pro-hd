namespace WeraPro.Application.Finance;

using WeraPro.Application.Abstractions;
using WeraPro.Application.Sales;
using WeraPro.Domain.Finance;
using WeraPro.Domain.Security;
using WeraPro.Domain.SharedKernel;

// ═══════════════════════════════════════════════════════════════════════════
// حالات استخدام G8 (§14/§13/§9.9): إغلاق الوردية ومطابقتها · الخزائن والسندات ·
// المصروفات · رأس المال · طباعة مستندات G8 بخصوصية الرواتب (§31/§35.2).
// كل الأوامر خادمية بذرات صلاحية — الواجهة ليست مصدر الحقيقة (§9.0 ق10).
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>الخزائن والأدراج (§14.2): تعريف وعرض بأرصدة الدفتر المنفصلة.</summary>
public sealed class ManageTreasuryUseCase(IFinanceRepository finance)
{
    public async Task<Result<IReadOnlyList<Contracts.V1.TreasuryAccountBalanceDto>>> ListAsync(bool includeInactive, CancellationToken ct = default)
    {
        var accounts = await finance.ListTreasuryAccountsAsync(!includeInactive, ct);
        return Result.Ok<IReadOnlyList<Contracts.V1.TreasuryAccountBalanceDto>>(ToDto(accounts));
    }

    public async Task<Result<Contracts.V1.TreasuryAccountBalanceDto>> CreateAsync(
        string? code, string? nameAr, string? accountKind, long? branchId, long? stationId, string? correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Length < 2)
        {
            return Result.Fail<Contracts.V1.TreasuryAccountBalanceDto>(new("TREASURY_CODE_INVALID", "كود الحساب لا يقل عن محرفين"));
        }
        if (string.IsNullOrWhiteSpace(nameAr) || nameAr.Trim().Length < 3)
        {
            return Result.Fail<Contracts.V1.TreasuryAccountBalanceDto>(new("TREASURY_NAME_INVALID", "اسم الحساب لا يقل عن 3 محارف"));
        }
        var kinds = new[] { "CASH_DRAWER", "SAFE", "BANK", "PAYMENT_PROVIDER" };
        if (string.IsNullOrWhiteSpace(accountKind) || !kinds.Contains(accountKind))
        {
            return Result.Fail<Contracts.V1.TreasuryAccountBalanceDto>(new("TREASURY_KIND_INVALID", "نوع الحساب: درج/خزينة/بنك/مزود دفع"));
        }
        if (accountKind == "CASH_DRAWER" && stationId is null && branchId is null)
        {
            return Result.Fail<Contracts.V1.TreasuryAccountBalanceDto>(new("TREASURY_DRAWER_SCOPE_REQUIRED", "الدرج يرتبط بمحطة أو فرع (§14.2)"));
        }
        try
        {
            var created = await finance.CreateTreasuryAccountAsync(code.Trim(), nameAr.Trim(), accountKind, branchId, stationId, ct);
            if (!created.IsSuccess)
            {
                return Result.Fail<Contracts.V1.TreasuryAccountBalanceDto>(created.Error!);
            }
            return Result.Ok(ToDto(created.Value!));
        }
        catch (FinancePolicyException ex)
        {
            return Result.Fail<Contracts.V1.TreasuryAccountBalanceDto>(new(ex.Code, ex.UserMessageAr));
        }
    }

    public async Task<Result<Contracts.V1.TreasuryTxnPagedDto>> ListTransactionsAsync(
        long? treasuryAccountId, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int page, int pageSize, CancellationToken ct = default)
    {
        if (treasuryAccountId is null)
        {
            return Result.Fail<Contracts.V1.TreasuryTxnPagedDto>(new("TREASURY_ACCOUNT_REQUIRED", "حدد حساب الخزينة لعرض حركاته"));
        }
        var (items, total) = await finance.ListTreasuryTransactionsAsync(treasuryAccountId, fromUtc, toUtc, page, pageSize, ct);
        return Result.Ok(new Contracts.V1.TreasuryTxnPagedDto(
            items.Select(t => new Contracts.V1.TreasuryTxnDto(
                t.TxnId, t.TreasuryAccountId, t.AccountCode, t.AccountNameAr, t.AccountKind,
                t.Direction, t.Amount, t.SourceDocType, t.SourceDocId, t.ShiftId,
                t.Reason, t.OccurredAtUtc, t.CreatedByNameAr)).ToList(),
            total, page, pageSize));
    }

    internal static Contracts.V1.TreasuryAccountBalanceDto ToDto(TreasuryAccountData a) => new(
        a.TreasuryAccountId, a.Code, a.NameAr, a.AccountKind, a.BranchId, a.StationId, a.IsActive, a.CurrentBalance);

    private static List<Contracts.V1.TreasuryAccountBalanceDto> ToDto(IReadOnlyList<TreasuryAccountData> accounts) =>
        accounts.Select(ToDto).ToList();
}

/// <summary>السندات: قبض/صرف/تحويل (§14.2) بتوزيع سند القبض على فواتير العميل ≤ المتبقي (§13/PRT-003).</summary>
public sealed class ManageVouchersUseCase(
    IFinanceRepository finance, ISalesRepository sales, IAuditWriter audit, ICurrentUser user)
{
    public async Task<Result<Contracts.V1.VoucherDto>> CreateAsync(
        Contracts.V1.CreateVoucherRequest request, string? correlationId, CancellationToken ct = default)
    {
        var treasury = await finance.GetTreasuryAccountAsync(request.TreasuryAccountId, ct);
        if (treasury is null || !treasury.IsActive)
        {
            return Result.Fail<Contracts.V1.VoucherDto>(new("TREASURY_ACCOUNT_NOT_FOUND", "حساب الخزينة غير موجود أو معطل"));
        }
        var counter = request.CounterTreasuryAccountId is { } cid
            ? await finance.GetTreasuryAccountAsync(cid, ct)
            : null;
        if (request.CounterTreasuryAccountId is not null && (counter is null || !counter.IsActive))
        {
            return Result.Fail<Contracts.V1.VoucherDto>(new("COUNTER_TREASURY_NOT_FOUND", "خزينة الوجهة غير موجودة أو معطلة"));
        }

        var validation = VoucherRules.ValidateCreate(
            request.VoucherType, request.Amount, request.TreasuryAccountId,
            request.CounterTreasuryAccountId, request.CounterpartyType, request.CounterpartyId, request.ReasonAr ?? string.Empty);
        if (validation is not null)
        {
            return Result.Fail<Contracts.V1.VoucherDto>(validation);
        }

        // الطرف الموثق موجود فعلاً (لا معرفات معلقة)
        if (request.CounterpartyType == "CUSTOMER" && !await finance.CustomerExistsAsync(request.CounterpartyId!.Value, ct))
        {
            return Result.Fail<Contracts.V1.VoucherDto>(new("CUSTOMER_NOT_FOUND", "العميل غير موجود"));
        }
        if (request.CounterpartyType == "SUPPLIER" && !await finance.SupplierExistsAsync(request.CounterpartyId!.Value, ct))
        {
            return Result.Fail<Contracts.V1.VoucherDto>(new("SUPPLIER_NOT_FOUND", "المورد غير موجود"));
        }
        if (request.CounterpartyType == "EMPLOYEE" && !await finance.UserExistsAsync(request.CounterpartyId!.Value, ct))
        {
            return Result.Fail<Contracts.V1.VoucherDto>(new("EMPLOYEE_NOT_FOUND", "الموظف غير موجود"));
        }

        // التوزيع على الفواتير: لسند قبض من عميل فقط، وكل توزيع ≤ متبقي فاتورته (§13)
        if (request.Allocations is { Count: > 0 })
        {
            if (!VoucherRules.MayAllocate(request.VoucherType, request.CounterpartyType))
            {
                return Result.Fail<Contracts.V1.VoucherDto>(new("VOUCHER_ALLOCATION_NOT_ALLOWED", "التوزيع على الفواتير لسند قبض من عميل فقط (§13)"));
            }
            var receivables = await finance.GetCustomerReceivablesAsync(request.CounterpartyId!.Value, ct);
            var requestedTotal = 0m;
            foreach (var allocation in request.Allocations)
            {
                var receivable = receivables.FirstOrDefault(r => r.InvoiceId == allocation.InvoiceId);
                if (receivable is null)
                {
                    return Result.Fail<Contracts.V1.VoucherDto>(new("ALLOCATION_INVOICE_NOT_ELIGIBLE",
                        $"الفاتورة {allocation.InvoiceId} ليست فاتورة آجلة قائمة لهذا العميل"));
                }
                var check = VoucherRules.ValidateAllocation(allocation.Amount, receivable.Remaining);
                if (check is not null)
                {
                    return Result.Fail<Contracts.V1.VoucherDto>(check);
                }
                requestedTotal += allocation.Amount;
            }
            if (requestedTotal > request.Amount + 0.00005m)
            {
                return Result.Fail<Contracts.V1.VoucherDto>(new("VOUCHER_OVER_ALLOCATED",
                    $"مجموع التوزيعات ({requestedTotal:0.00}) يتجاوز مبلغ السند ({request.Amount:0.00}) — §13"));
            }
        }

        // سياق الوردية إلزامي لعمليات الدرج (المتوقع في الدرج يطابق كل حركاته — بوابة القبول)
        long? shiftId = null;
        if (InvolvesDrawer(treasury, counter))
        {
            if (user.StationId is not { } stationId)
            {
                return Result.Fail<Contracts.V1.VoucherDto>(new("DRAWER_OPERATION_REQUIRES_STATION",
                    "عمليات درج النقد تتطلب جلسة محطة (ربط الحركة بورديتها §14.1)"));
            }
            var current = await sales.GetCurrentShiftAsync(stationId, ct);
            if (current is null)
            {
                return Result.Fail<Contracts.V1.VoucherDto>(new("DRAWER_OPERATION_REQUIRES_OPEN_SHIFT",
                    "لا وردية مفتوحة لمحطتك — عمليات الدرج تُوثّق داخل وردية (§14.1)"));
            }
            shiftId = current.ShiftId;
        }

        var number = await finance.NextVoucherNumberAsync(request.VoucherType, ct);
        try
        {
            var created = await finance.CreateVoucherAsync(new VoucherCreatePlan(
                request.VoucherType, number, request.TreasuryAccountId, request.CounterTreasuryAccountId,
                request.Amount, request.CounterpartyType, request.CounterpartyId,
                request.ReasonAr!.Trim(), user.UserId, user.BranchId ?? treasury.BranchId ?? 0, shiftId,
                (request.Allocations ?? []).Select(a => new VoucherAllocationPlan(a.InvoiceId, a.Amount)).ToList()), ct);
            if (!created.IsSuccess)
            {
                return Result.Fail<Contracts.V1.VoucherDto>(created.Error!);
            }
            await audit.WriteAsync(new AuditEntry(
                UserId: user.UserId, RoleCode: null, StationId: user.StationId, BranchId: user.BranchId,
                EntitySchema: "finance.vouchers", EntityId: created.Value!.VoucherId, Action: "VOUCHER_CREATE", Result: "SUCCESS",
                Reason: $"{request.VoucherType} بمبلغ {request.Amount:0.00} — {request.ReasonAr.Trim()}",
                CorrelationId: correlationId), ct);
            return Result.Ok(ToDto(created.Value!));
        }
        catch (FinancePolicyException ex)
        {
            return Result.Fail<Contracts.V1.VoucherDto>(new(ex.Code, ex.UserMessageAr));
        }
    }

    public async Task<Result<Contracts.V1.VoucherDto>> PostAsync(long voucherId, string? correlationId, CancellationToken ct = default)
    {
        var voucher = await finance.GetVoucherAsync(voucherId, ct);
        if (voucher is null)
        {
            return Result.Fail<Contracts.V1.VoucherDto>(new("VOUCHER_NOT_FOUND", "السند غير موجود"));
        }
        if (voucher.Status != "DRAFT")
        {
            return Result.Fail<Contracts.V1.VoucherDto>(new("VOUCHER_NOT_DRAFT", $"السند في حالة {voucher.Status} — لا ترحيل مكرر (§14.2)"));
        }
        // سياق الوردية لعمليات الدرج عند الترحيل (حركة الدرج تحمل وردية صحيحة)
        long? shiftId = null;
        var treasury = await finance.GetTreasuryAccountAsync(voucher.TreasuryAccountId, ct);
        var counter = voucher.CounterTreasuryAccountId is { } cid ? await finance.GetTreasuryAccountAsync(cid, ct) : null;
        if (InvolvesDrawer(treasury, counter))
        {
            if (user.StationId is not { } stationId)
            {
                return Result.Fail<Contracts.V1.VoucherDto>(new("DRAWER_OPERATION_REQUIRES_STATION",
                    "عمليات درج النقد تتطلب جلسة محطة (ربط الحركة بورديتها §14.1)"));
            }
            var current = await sales.GetCurrentShiftAsync(stationId, ct);
            if (current is null)
            {
                return Result.Fail<Contracts.V1.VoucherDto>(new("DRAWER_OPERATION_REQUIRES_OPEN_SHIFT",
                    "لا وردية مفتوحة لمحطتك — عمليات الدرج تُوثّق داخل وردية (§14.1)"));
            }
            shiftId = current.ShiftId;
        }
        try
        {
            var posted = await finance.PostVoucherAsync(new VoucherPostPlan(voucherId, user.UserId, shiftId, DateTimeOffset.UtcNow), ct);
            if (!posted.IsSuccess)
            {
                return Result.Fail<Contracts.V1.VoucherDto>(posted.Error!);
            }
            await audit.WriteAsync(new AuditEntry(
                UserId: user.UserId, RoleCode: null, StationId: user.StationId, BranchId: user.BranchId,
                EntitySchema: "finance.vouchers", EntityId: voucherId, Action: "VOUCHER_POST", Result: "SUCCESS",
                Reason: $"ترحيل {voucher.VoucherNumber} ({voucher.VoucherType}) بمبلغ {voucher.Amount:0.00}",
                CorrelationId: correlationId), ct);
            return Result.Ok(ToDto(posted.Value!));
        }
        catch (FinancePolicyException ex)
        {
            return Result.Fail<Contracts.V1.VoucherDto>(new(ex.Code, ex.UserMessageAr));
        }
    }

    public async Task<Result<long>> CancelAsync(long voucherId, string? reasonAr, string? correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reasonAr) || reasonAr.Trim().Length < 3)
        {
            return Result.Fail<long>(new("CANCEL_REASON_REQUIRED", "إلغاء السند يستلزم سبباً موثقاً"));
        }
        var voucher = await finance.GetVoucherAsync(voucherId, ct);
        if (voucher is null)
        {
            return Result.Fail<long>(new("VOUCHER_NOT_FOUND", "السند غير موجود"));
        }
        if (voucher.Status != "DRAFT")
        {
            return Result.Fail<long>(new("VOUCHER_NOT_DRAFT",
                "لا يُلغى إلا سند مسودة — المرحّل نهائي والتصحيح بسند معاكس موثق (§14.2)"));
        }
        try
        {
            var cancelled = await finance.CancelVoucherDraftAsync(voucherId, user.UserId, reasonAr.Trim(), ct);
            if (!cancelled.IsSuccess)
            {
                return Result.Fail<long>(cancelled.Error!);
            }
            await audit.WriteAsync(new AuditEntry(
                UserId: user.UserId, RoleCode: null, StationId: user.StationId, BranchId: user.BranchId,
                EntitySchema: "finance.vouchers", EntityId: voucherId, Action: "VOUCHER_CANCEL", Result: "SUCCESS",
                Reason: reasonAr.Trim(), CorrelationId: correlationId), ct);
            return Result.Ok(voucherId);
        }
        catch (FinancePolicyException ex)
        {
            return Result.Fail<long>(new(ex.Code, ex.UserMessageAr));
        }
    }

    public async Task<Result<Contracts.V1.VoucherDto>> GetAsync(long voucherId, CancellationToken ct = default)
    {
        var voucher = await finance.GetVoucherAsync(voucherId, ct);
        return voucher is null
            ? Result.Fail<Contracts.V1.VoucherDto>(new("VOUCHER_NOT_FOUND", "السند غير موجود"))
            : Result.Ok(ToDto(voucher));
    }

    public async Task<Contracts.V1.VoucherPagedDto> ListAsync(
        string? voucherType, string? status, int page, int pageSize, CancellationToken ct = default)
    {
        var (items, total) = await finance.ListVouchersAsync(voucherType, status, page, pageSize, ct);
        return new Contracts.V1.VoucherPagedDto(items.Select(ToDto).ToList(), total, page, pageSize);
    }

    public async Task<Result<IReadOnlyList<Contracts.V1.CustomerReceivableDto>>> GetReceivablesAsync(long customerId, CancellationToken ct = default)
    {
        if (!await finance.CustomerExistsAsync(customerId, ct))
        {
            return Result.Fail<IReadOnlyList<Contracts.V1.CustomerReceivableDto>>(new("CUSTOMER_NOT_FOUND", "العميل غير موجود"));
        }
        var receivables = await finance.GetCustomerReceivablesAsync(customerId, ct);
        return Result.Ok<IReadOnlyList<Contracts.V1.CustomerReceivableDto>>(receivables.Select(r => new Contracts.V1.CustomerReceivableDto(
            r.InvoiceId, r.InvoiceNumber, r.PostedAtUtc, r.Status, r.DueAmount, r.AllocatedPosted, r.Remaining)).ToList());
    }

    private static bool InvolvesDrawer(TreasuryAccountData? a, TreasuryAccountData? b) =>
        a?.AccountKind == "CASH_DRAWER" || b?.AccountKind == "CASH_DRAWER";

    internal static Contracts.V1.VoucherDto ToDto(VoucherData v) => new(
        v.VoucherId, v.VoucherNumber, v.VoucherType, v.Status,
        v.TreasuryAccountId, v.TreasuryCode, v.TreasuryNameAr,
        v.CounterTreasuryAccountId, v.CounterTreasuryCode,
        v.Amount, v.CounterpartyType, v.CounterpartyId, v.CounterpartyNameAr,
        v.Reason, v.ShiftId, v.CreatedAtUtc, v.PostedAtUtc,
        v.Allocations.Select(a => new Contracts.V1.VoucherAllocationDto(a.InvoiceId, a.InvoiceNumber, a.Amount, a.InvoiceRemainingAfter)).ToList());
}

/// <summary>المصروفات (§14.4): اعتماد ثم سداد، وأثر مالي واحد، وإلغاء المرحَّل بعكس موثق.</summary>
public sealed class ManageExpensesUseCase(
    IFinanceRepository finance, IAuditWriter audit, ICurrentUser user)
{
    public async Task<Result<Contracts.V1.ExpenseDto>> CreateAsync(
        Contracts.V1.CreateExpenseRequest request, string? correlationId, CancellationToken ct = default)
    {
        if (request.Amount <= 0m)
        {
            return Result.Fail<Contracts.V1.ExpenseDto>(new("EXPENSE_AMOUNT_INVALID", "مبلغ المصروف يجب أن يكون أكبر من صفر"));
        }
        if (request.TaxAmount < 0m)
        {
            return Result.Fail<Contracts.V1.ExpenseDto>(new("EXPENSE_TAX_INVALID", "ضريبة المصروف لا تكون سالبة"));
        }
        var categories = await finance.ListExpenseCategoriesAsync(true, ct);
        if (categories.All(c => c.CategoryId != request.CategoryId))
        {
            return Result.Fail<Contracts.V1.ExpenseDto>(new("EXPENSE_CATEGORY_NOT_FOUND", "فئة المصروف غير موجودة أو معطلة"));
        }
        var methods = new[] { "CASH", "BANK_TRANSFER", "CHEQUE", "CARD" };
        if (string.IsNullOrWhiteSpace(request.PaymentMethod) || !methods.Contains(request.PaymentMethod))
        {
            return Result.Fail<Contracts.V1.ExpenseDto>(new("EXPENSE_METHOD_INVALID", "وسيلة السداد: نقدي/تحويل/شيك/بطاقة"));
        }

        TreasuryAccountData? treasury = null;
        if (request.TreasuryAccountId is { } accountId)
        {
            treasury = await finance.GetTreasuryAccountAsync(accountId, ct);
            if (treasury is null || !treasury.IsActive)
            {
                return Result.Fail<Contracts.V1.ExpenseDto>(new("TREASURY_ACCOUNT_NOT_FOUND", "حساب الخزينة غير موجود أو معطل"));
            }
        }

        // مصروف وردية من درج المحطة (§14.1/§32 — القاعدة الموثقة): يرحَّل مباشرة داخل وردية مفتوحة
        var postDirectly = false;
        if (request.ShiftId is { } shiftId)
        {
            var shift = await finance.GetShiftAsync(shiftId, ct);
            if (shift is null || shift.Status != "OPEN")
            {
                return Result.Fail<Contracts.V1.ExpenseDto>(new("EXPENSE_SHIFT_NOT_OPEN", "ربط المصروف يستلزم وردية مفتوحة"));
            }
            var drawer = await finance.FindDrawerAccountAsync(shift.StationId, ct);
            if (drawer is null || treasury is null || treasury.TreasuryAccountId != drawer.TreasuryAccountId)
            {
                return Result.Fail<Contracts.V1.ExpenseDto>(new("EXPENSE_SHIFT_DRAWER_REQUIRED",
                    "مصروف الوردية يُدفع من درج محطتها تحديداً (§32 — فصل مصروف الوردية المدفوع من الدرج)"));
            }
            postDirectly = ExpenseRules.MayPostDirectly(shiftId, treasury.TreasuryAccountId, drawer.TreasuryAccountId);
        }

        var number = await finance.NextExpenseNumberAsync(ct);
        try
        {
            var created = await finance.CreateExpenseAsync(new ExpenseCreatePlan(
                number, request.CategoryId, user.BranchId ?? 0, request.ExpenseDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
                request.Amount, request.TaxAmount, request.PaymentMethod, request.TreasuryAccountId,
                string.IsNullOrWhiteSpace(request.Beneficiary) ? null : request.Beneficiary.Trim(),
                string.IsNullOrWhiteSpace(request.CostCenter) ? null : request.CostCenter.Trim(),
                request.ShiftId, string.IsNullOrWhiteSpace(request.NotesAr) ? null : request.NotesAr.Trim(),
                user.UserId, postDirectly), ct);
            if (!created.IsSuccess)
            {
                return Result.Fail<Contracts.V1.ExpenseDto>(created.Error!);
            }
            await audit.WriteAsync(new AuditEntry(
                UserId: user.UserId, RoleCode: null, StationId: user.StationId, BranchId: user.BranchId,
                EntitySchema: "finance.expenses", EntityId: created.Value!.ExpenseId,
                Action: postDirectly ? "EXPENSE_CREATE_AND_POST" : "EXPENSE_CREATE", Result: "SUCCESS",
                Reason: $"مبلغ {request.Amount:0.00} ({request.PaymentMethod})" + (request.ShiftId is null ? "" : $" — وردية {request.ShiftId}"),
                CorrelationId: correlationId), ct);
            return Result.Ok(ToDto(created.Value!));
        }
        catch (FinancePolicyException ex)
        {
            return Result.Fail<Contracts.V1.ExpenseDto>(new(ex.Code, ex.UserMessageAr));
        }
    }

    public async Task<Result<Contracts.V1.ExpenseDto>> ApproveAsync(
        long expenseId, string? reasonAr, string? correlationId, CancellationToken ct = default)
    {
        var expense = await finance.GetExpenseAsync(expenseId, ct);
        if (expense is null)
        {
            return Result.Fail<Contracts.V1.ExpenseDto>(new("EXPENSE_NOT_FOUND", "المصروف غير موجود"));
        }
        if (expense.Status != "DRAFT")
        {
            return Result.Fail<Contracts.V1.ExpenseDto>(new("EXPENSE_NOT_DRAFT", "الاعتماد لمصروف مسودة فقط"));
        }
        try
        {
            // اعتماد ثنائي موثق في core.approvals (المعتمد ≠ المنشئ — قيد no_self) — ينشئه المستودع ويقيده على المصروف
            var approved = await finance.ApproveExpenseAsync(expenseId, user.UserId, reasonAr, ct);
            if (!approved.IsSuccess)
            {
                return Result.Fail<Contracts.V1.ExpenseDto>(approved.Error!);
            }
            await audit.WriteAsync(new AuditEntry(
                UserId: user.UserId, RoleCode: null, StationId: user.StationId, BranchId: user.BranchId,
                EntitySchema: "finance.expenses", EntityId: expenseId, Action: "EXPENSE_APPROVE", Result: "SUCCESS",
                Reason: reasonAr, CorrelationId: correlationId), ct);
            return Result.Ok(ToDto(approved.Value!));
        }
        catch (FinancePolicyException ex)
        {
            return Result.Fail<Contracts.V1.ExpenseDto>(new(ex.Code, ex.UserMessageAr));
        }
    }

    public async Task<Result<Contracts.V1.ExpensePostResponse>> PostAsync(
        long expenseId, string? correlationId, CancellationToken ct = default)
    {
        var expense = await finance.GetExpenseAsync(expenseId, ct);
        if (expense is null)
        {
            return Result.Fail<Contracts.V1.ExpensePostResponse>(new("EXPENSE_NOT_FOUND", "المصروف غير موجود"));
        }
        if (expense.Status == "POSTED")
        {
            return Result.Fail<Contracts.V1.ExpensePostResponse>(new("EXPENSE_ALREADY_POSTED", "المصروف مرحّل — لا ترحيل مكرر (§14.4)"));
        }
        if (expense.Status == "CANCELLED")
        {
            return Result.Fail<Contracts.V1.ExpensePostResponse>(new("EXPENSE_CANCELLED", "المصروف ملغى"));
        }
        if (expense.TreasuryAccountId is null)
        {
            return Result.Fail<Contracts.V1.ExpensePostResponse>(new("EXPENSE_TREASURY_REQUIRED", "السداد يستلزم حساب خزينة — لا ترحيل مصروف بلا وجهة دفع"));
        }
        if (expense.Status == "DRAFT")
        {
            // DRAFT→POSTED مباشر لمصروف الوردية من الدرج فقط (القاعدة الموثقة G8) —
            // وما سواه اعتمادٌ ثم ترحيل (§14.4)
            var shift = expense.ShiftId is { } sid ? await finance.GetShiftAsync(sid, ct) : null;
            if (shift is null)
            {
                return Result.Fail<Contracts.V1.ExpensePostResponse>(new("EXPENSE_APPROVAL_REQUIRED", "المصروف العام يُعتمد قبل ترحيله (§14.4)"));
            }
            var drawer = await finance.FindDrawerAccountAsync(shift.StationId, ct);
            if (drawer is null || drawer.TreasuryAccountId != expense.TreasuryAccountId)
            {
                return Result.Fail<Contracts.V1.ExpensePostResponse>(new("EXPENSE_APPROVAL_REQUIRED", "الترحيل المباشر لمصروف وردية من درج محطتها فقط — اعتمد المصروف أولاً (§14.4)"));
            }
        }
        try
        {
            var posted = await finance.PostExpenseAsync(new ExpensePostPlan(
                expenseId, user.UserId, expense.ShiftId, ExpenseRules.CashOutflow(expense.Amount, expense.TaxAmount), DateTimeOffset.UtcNow), ct);
            if (!posted.IsSuccess)
            {
                return Result.Fail<Contracts.V1.ExpensePostResponse>(posted.Error!);
            }
            await audit.WriteAsync(new AuditEntry(
                UserId: user.UserId, RoleCode: null, StationId: user.StationId, BranchId: user.BranchId,
                EntitySchema: "finance.expenses", EntityId: expenseId, Action: "EXPENSE_POST", Result: "SUCCESS",
                Reason: $"ترحيل {expense.ExpenseNumber} بأثر مالي {ExpenseRules.CashOutflow(expense.Amount, expense.TaxAmount):0.00}",
                CorrelationId: correlationId), ct);

            // §29.10: سند المصروف التشغيلي AUTO_AFTER_POST على طابعة المالية — إن لم تكن معينة
            // للمحطة فإشعار صادق بلا مهمة (لا فشل صامت) — الرندر الفيزيائي G10 (B-07)
            PrintOutcome print = new(false, null, null, null, null, "لم تُعيَّن طابعة مالية لمحطتك — المستند محفوظ والطباعة عند الطلب (§29.10)");
            if (user.StationId is { } stationId)
            {
                print = await finance.CreateFinancePrintJobAsync(stationId, user.UserId, "EXPENSE_VOUCHER", expenseId, "ORIGINAL", null, ct);
            }
            return Result.Ok(new Contracts.V1.ExpensePostResponse(ToDto(posted.Value!), print.JobCreated, print.JobId, print.JobStatus, print.MediaClass, print.HoldReasonCode, print.NoticeAr));
        }
        catch (FinancePolicyException ex)
        {
            return Result.Fail<Contracts.V1.ExpensePostResponse>(new(ex.Code, ex.UserMessageAr));
        }
    }

    public async Task<Result<Contracts.V1.ExpenseDto>> CancelAsync(
        long expenseId, string? reasonAr, string? correlationId, CancellationToken ct = default)
    {
        if (!ExpenseRules.ReversalReasonIsValid(reasonAr))
        {
            return Result.Fail<Contracts.V1.ExpenseDto>(new("EXPENSE_CANCEL_REASON_REQUIRED", "إلغاء المصروف يستلزم سبباً موثقاً لا يقل عن 3 محارف (§14.4)"));
        }
        var expense = await finance.GetExpenseAsync(expenseId, ct);
        if (expense is null)
        {
            return Result.Fail<Contracts.V1.ExpenseDto>(new("EXPENSE_NOT_FOUND", "المصروف غير موجود"));
        }
        try
        {
            var cancelled = await finance.CancelExpenseAsync(expenseId, user.UserId, reasonAr!.Trim(), ct);
            if (!cancelled.IsSuccess)
            {
                return Result.Fail<Contracts.V1.ExpenseDto>(cancelled.Error!);
            }
            await audit.WriteAsync(new AuditEntry(
                UserId: user.UserId, RoleCode: null, StationId: user.StationId, BranchId: user.BranchId,
                EntitySchema: "finance.expenses", EntityId: expenseId,
                Action: expense.Status == "POSTED" ? "EXPENSE_REVERSE" : "EXPENSE_CANCEL", Result: "SUCCESS",
                Reason: reasonAr.Trim(), CorrelationId: correlationId), ct);
            return Result.Ok(ToDto(cancelled.Value!));
        }
        catch (FinancePolicyException ex)
        {
            return Result.Fail<Contracts.V1.ExpenseDto>(new(ex.Code, ex.UserMessageAr));
        }
    }

    public async Task<Contracts.V1.ExpensePagedDto> ListAsync(
        string? status, long? shiftId, int page, int pageSize, CancellationToken ct = default)
    {
        var (items, total) = await finance.ListExpensesAsync(status, shiftId, page, pageSize, ct);
        return new Contracts.V1.ExpensePagedDto(items.Select(ToDto).ToList(), total, page, pageSize);
    }

    public async Task<IReadOnlyList<Contracts.V1.ExpenseCategoryDto>> ListCategoriesAsync(CancellationToken ct = default)
    {
        var categories = await finance.ListExpenseCategoriesAsync(false, ct);
        return categories.Select(c => new Contracts.V1.ExpenseCategoryDto(c.CategoryId, c.Code, c.NameAr, c.IsActive)).ToList();
    }

    public async Task<Result<Contracts.V1.ExpenseCategoryDto>> CreateCategoryAsync(
        string? code, string? nameAr, string? correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Length < 2)
        {
            return Result.Fail<Contracts.V1.ExpenseCategoryDto>(new("CATEGORY_CODE_INVALID", "كود الفئة لا يقل عن محرفين"));
        }
        if (string.IsNullOrWhiteSpace(nameAr) || nameAr.Trim().Length < 3)
        {
            return Result.Fail<Contracts.V1.ExpenseCategoryDto>(new("CATEGORY_NAME_INVALID", "اسم الفئة لا يقل عن 3 محارف"));
        }
        try
        {
            var created = await finance.CreateExpenseCategoryAsync(code.Trim(), nameAr.Trim(), ct);
            if (!created.IsSuccess)
            {
                return Result.Fail<Contracts.V1.ExpenseCategoryDto>(created.Error!);
            }
            await audit.WriteAsync(new AuditEntry(
                UserId: user.UserId, RoleCode: null, StationId: user.StationId, BranchId: user.BranchId,
                EntitySchema: "finance.expense_categories", EntityId: created.Value!.CategoryId, Action: "EXPENSE_CATEGORY_CREATE", Result: "SUCCESS",
                Reason: nameAr, CorrelationId: correlationId), ct);
            return Result.Ok(new Contracts.V1.ExpenseCategoryDto(created.Value.CategoryId, created.Value.Code, created.Value.NameAr, created.Value.IsActive));
        }
        catch (FinancePolicyException ex)
        {
            return Result.Fail<Contracts.V1.ExpenseCategoryDto>(new(ex.Code, ex.UserMessageAr));
        }
    }

    internal static Contracts.V1.ExpenseDto ToDto(ExpenseData e) => new(
        e.ExpenseId, e.ExpenseNumber, e.Status, e.CategoryId, e.CategoryNameAr, e.BranchId, e.ExpenseDate,
        e.Amount, e.TaxAmount, e.CashOutflow, e.PaymentMethod,
        e.TreasuryAccountId, e.TreasuryCode, e.TreasuryNameAr,
        e.Beneficiary, e.CostCenter, e.ShiftId, e.ApprovalId, e.Notes, e.CreatedByUserId, e.CreatedAtUtc, e.PostedAtUtc);
}

/// <summary>رأس المال والأرصدة الافتتاحية (§14.3/D-14) — أحداث موثقة، والافتتاحي باعتماد فقط.</summary>
public sealed class ManageCapitalUseCase(IFinanceRepository finance, IAuditWriter audit, ICurrentUser user)
{
    public async Task<Result<Contracts.V1.CapitalEventDto>> CreateAsync(
        Contracts.V1.CreateCapitalEventRequest request, string? correlationId, CancellationToken ct = default)
    {
        if (!CapitalRules.EventTypes.Contains(request.EventType))
        {
            return Result.Fail<Contracts.V1.CapitalEventDto>(new("CAPITAL_EVENT_TYPE_INVALID", "نوع الحدث غير معروف (مساهمة/سحب/تمويل/افتتاحي)"));
        }
        if (request.Amount <= 0m)
        {
            return Result.Fail<Contracts.V1.CapitalEventDto>(new("CAPITAL_AMOUNT_INVALID", "مبلغ الحدث يجب أن يكون أكبر من صفر"));
        }
        if (string.IsNullOrWhiteSpace(request.DescriptionAr) || request.DescriptionAr.Trim().Length < 3)
        {
            return Result.Fail<Contracts.V1.CapitalEventDto>(new("CAPITAL_DESCRIPTION_REQUIRED", "وصف الحدث موثق لا يقل عن 3 محارف (§14.3)"));
        }
        TreasuryAccountData? treasury = null;
        if (request.TreasuryAccountId is { } accountId)
        {
            treasury = await finance.GetTreasuryAccountAsync(accountId, ct);
            if (treasury is null || !treasury.IsActive)
            {
                return Result.Fail<Contracts.V1.CapitalEventDto>(new("TREASURY_ACCOUNT_NOT_FOUND", "حساب الخزينة غير موجود أو معطل"));
            }
        }
        if (CapitalRules.RequiresApproval(request.EventType))
        {
            // D-14/B-04: الرصيد الافتتاحي لا يُنشأ إلا باعتماد موثق مسبق (قيد V006) —
            // وآلية «بانتظار اعتماد» تعني مستنداً موثقاً بلا أرقام مخفية في الكود
            if (request.ApprovalId is not { } approvalId)
            {
                return Result.Fail<Contracts.V1.CapitalEventDto>(new("CAPITAL_OPENING_APPROVAL_REQUIRED",
                    "الرصيد الافتتاحي يستلزم اعتماداً موثقاً مسبقاً — الأرصدة بمستندات معتمدة فقط (D-14/§14.3)"));
            }
            if (!await finance.IsCapitalApprovalApprovedAsync(approvalId, ct))
            {
                return Result.Fail<Contracts.V1.CapitalEventDto>(new("CAPITAL_OPENING_APPROVAL_NOT_APPROVED",
                    "اعتماد الرصيد الافتتاحي غير مقرر بعد — راجع صاحب القرار (D-14/B-04)"));
            }
        }
        var number = await finance.NextCapitalEventNumberAsync(ct);
        try
        {
            var created = await finance.CreateCapitalEventAsync(new CapitalCreatePlan(
                number, request.EventType, request.Amount, request.TreasuryAccountId,
                request.EventDate ?? DateOnly.FromDateTime(DateTime.UtcNow), request.DescriptionAr.Trim(),
                request.ApprovalId, user.UserId, user.BranchId ?? 0), ct);
            if (!created.IsSuccess)
            {
                return Result.Fail<Contracts.V1.CapitalEventDto>(created.Error!);
            }
            await audit.WriteAsync(new AuditEntry(
                UserId: user.UserId, RoleCode: null, StationId: user.StationId, BranchId: user.BranchId,
                EntitySchema: "finance.capital_events", EntityId: created.Value!.CapitalId, Action: "CAPITAL_CREATE", Result: "SUCCESS",
                Reason: $"{request.EventType} بمبلغ {request.Amount:0.00} — {request.DescriptionAr.Trim()}",
                CorrelationId: correlationId), ct);
            return Result.Ok(ToDto(created.Value!));
        }
        catch (FinancePolicyException ex)
        {
            return Result.Fail<Contracts.V1.CapitalEventDto>(new(ex.Code, ex.UserMessageAr));
        }
    }

    public async Task<Result<Contracts.V1.CapitalEventDto>> PostAsync(long capitalId, string? correlationId, CancellationToken ct = default)
    {
        var capitalEvent = await finance.GetCapitalEventAsync(capitalId, ct);
        if (capitalEvent is null)
        {
            return Result.Fail<Contracts.V1.CapitalEventDto>(new("CAPITAL_NOT_FOUND", "الحدث غير موجود"));
        }
        if (capitalEvent.Status != "DRAFT")
        {
            return Result.Fail<Contracts.V1.CapitalEventDto>(new("CAPITAL_NOT_DRAFT", "لا يُرحَّل إلا حدث مسودة — لا ترحيل مكرر (§14.3)"));
        }
        try
        {
            var posted = await finance.PostCapitalEventAsync(capitalId, user.UserId, user.BranchId, ct);
            if (!posted.IsSuccess)
            {
                return Result.Fail<Contracts.V1.CapitalEventDto>(posted.Error!);
            }
            await audit.WriteAsync(new AuditEntry(
                UserId: user.UserId, RoleCode: null, StationId: user.StationId, BranchId: user.BranchId,
                EntitySchema: "finance.capital_events", EntityId: capitalId, Action: "CAPITAL_POST", Result: "SUCCESS",
                Reason: $"ترحيل {capitalEvent.EventNumber} ({capitalEvent.EventType}) بمبلغ {capitalEvent.Amount:0.00}",
                CorrelationId: correlationId), ct);
            return Result.Ok(ToDto(posted.Value!));
        }
        catch (FinancePolicyException ex)
        {
            return Result.Fail<Contracts.V1.CapitalEventDto>(new(ex.Code, ex.UserMessageAr));
        }
    }

    public async Task<Result<long>> CancelAsync(long capitalId, string? reasonAr, string? correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reasonAr) || reasonAr.Trim().Length < 3)
        {
            return Result.Fail<long>(new("CANCEL_REASON_REQUIRED", "إلغاء الحدث يستلزم سبباً موثقاً"));
        }
        var capitalEvent = await finance.GetCapitalEventAsync(capitalId, ct);
        if (capitalEvent is null)
        {
            return Result.Fail<long>(new("CAPITAL_NOT_FOUND", "الحدث غير موجود"));
        }
        if (capitalEvent.Status != "DRAFT")
        {
            return Result.Fail<long>(new("CAPITAL_NOT_DRAFT", "لا يُلغى إلا حدث مسودة — المرحّل موثق نهائي (§14.3)"));
        }
        try
        {
            var cancelled = await finance.CancelCapitalEventDraftAsync(capitalId, user.UserId, reasonAr.Trim(), ct);
            if (!cancelled.IsSuccess)
            {
                return Result.Fail<long>(cancelled.Error!);
            }
            await audit.WriteAsync(new AuditEntry(
                UserId: user.UserId, RoleCode: null, StationId: user.StationId, BranchId: user.BranchId,
                EntitySchema: "finance.capital_events", EntityId: capitalId, Action: "CAPITAL_CANCEL", Result: "SUCCESS",
                Reason: reasonAr.Trim(), CorrelationId: correlationId), ct);
            return Result.Ok(capitalId);
        }
        catch (FinancePolicyException ex)
        {
            return Result.Fail<long>(new(ex.Code, ex.UserMessageAr));
        }
    }

    public async Task<Contracts.V1.CapitalPagedDto> ListAsync(
        string? eventType, string? status, int page, int pageSize, CancellationToken ct = default)
    {
        var (items, total) = await finance.ListCapitalEventsAsync(eventType, status, page, pageSize, ct);
        return new Contracts.V1.CapitalPagedDto(items.Select(ToDto).ToList(), total, page, pageSize);
    }

    internal static Contracts.V1.CapitalEventDto ToDto(CapitalEventData e) => new(
        e.CapitalId, e.EventNumber, e.EventType, e.Status, e.Amount,
        e.TreasuryAccountId, e.TreasuryCode, e.EventDate, e.Description, e.ApprovalId,
        e.CreatedAtUtc, e.PostedAtUtc);
}

/// <summary>
/// إغلاق الوردية ومراجعة النقدية (§14.1/§9.9 — استكمال G7): النقد المتوقع من الدفتر،
/// والفعلي من عدّ موثق، والفارق يظل ظاهراً حتى التسوية، والاعتماد بحسب الدور.
/// </summary>
public sealed class CloseShiftUseCase(
    IFinanceRepository finance, IUserRepository users, IAuditWriter audit, ICurrentUser user)
{
    public async Task<Result<Contracts.V1.ShiftReconciliationDto>> GetReconciliationAsync(long shiftId, CancellationToken ct = default)
    {
        var shift = await finance.GetShiftAsync(shiftId, ct);
        if (shift is null)
        {
            return Result.Fail<Contracts.V1.ShiftReconciliationDto>(new("SHIFT_NOT_FOUND", "الوردية غير موجودة"));
        }
        var drawer = await ResolveDrawerAsync(shift, ct);
        if (drawer is null)
        {
            return Result.Fail<Contracts.V1.ShiftReconciliationDto>(new("DRAWER_ACCOUNT_NOT_FOUND",
                "لا حساب درج نقد لمحطة هذه الوردية — عيّن درجاً للمحطة قبل المطابقة (§14.2)"));
        }
        var ledger = await finance.GetShiftDrawerLedgerAsync(shiftId, drawer.TreasuryAccountId, ct);
        if (ledger is null)
        {
            return Result.Fail<Contracts.V1.ShiftReconciliationDto>(new("SHIFT_NOT_FOUND", "الوردية غير موجودة"));
        }
        return Result.Ok(ToDto(shift, ledger));
    }

    public async Task<Result<Contracts.V1.ShiftCloseResponse>> CloseAsync(
        long shiftId, Contracts.V1.CloseShiftRequest request, string? correlationId, CancellationToken ct = default)
    {
        if (request.CountedCash < 0m)
        {
            return Result.Fail<Contracts.V1.ShiftCloseResponse>(new("COUNTED_CASH_INVALID", "النقدية المعدودة لا تكون سالبة"));
        }
        var shift = await finance.GetShiftAsync(shiftId, ct);
        if (shift is null)
        {
            return Result.Fail<Contracts.V1.ShiftCloseResponse>(new("SHIFT_NOT_FOUND", "الوردية غير موجودة"));
        }
        if (shift.Status != "OPEN")
        {
            return Result.Fail<Contracts.V1.ShiftCloseResponse>(new("SHIFT_ALREADY_CLOSED",
                "الوردية مغلقة بالفعل — لا إغلاق مرتين (§14.1)"));
        }
        var drawer = await ResolveDrawerAsync(shift, ct);
        if (drawer is null)
        {
            return Result.Fail<Contracts.V1.ShiftCloseResponse>(new("DRAWER_ACCOUNT_NOT_FOUND",
                "لا حساب درج نقد لمحطة هذه الوردية — عيّن درجاً للمحطة قبل الإغلاق (§14.2)"));
        }
        var ledger = await finance.GetShiftDrawerLedgerAsync(shiftId, drawer.TreasuryAccountId, ct);
        if (ledger is null)
        {
            return Result.Fail<Contracts.V1.ShiftCloseResponse>(new("SHIFT_NOT_FOUND", "الوردية غير موجودة"));
        }

        var expected = ledger.ExpectedInDrawer;
        var variance = ShiftReconciliation.Variance(request.CountedCash, expected);

        // اعتماد بحسب الدور (§14.1): الفارق يستلزم صلاحية اعتماد الفوارق + سبباً موثقاً
        if (ShiftReconciliation.RequiresReason(variance))
        {
            if (!ShiftReconciliation.ReasonIsValid(request.VarianceReasonAr))
            {
                return Result.Fail<Contracts.V1.ShiftCloseResponse>(new("SHIFT_VARIANCE_REASON_REQUIRED",
                    $"الفارق ({variance:0.00}) ج.م يستلزم سبباً موثقاً لا يقل عن 3 محارف — الفارق يبقى ظاهراً حتى التسوية"));
            }
            var perms = await users.GetPermissionCodesAsync(user.UserId, user.BranchId, ct);
            if (!perms.Contains(PermissionCodes.SalesShiftApproveVariance))
            {
                return Result.Fail<Contracts.V1.ShiftCloseResponse>(new("SHIFT_VARIANCE_APPROVAL_REQUIRED",
                    "إغلاق وردية بفارق يتطلب اعتماد مشرف فأعلى (sales.shift.approve_variance) — الكاشير لا يعتمد فارقه"));
            }
        }

        try
        {
            var closed = await finance.CloseShiftAsync(
                shiftId, user.UserId, expected, request.CountedCash,
                string.IsNullOrWhiteSpace(request.VarianceReasonAr) ? null : request.VarianceReasonAr.Trim(),
                string.IsNullOrWhiteSpace(request.NotesAr) ? null : request.NotesAr.Trim(), ct);
            if (!closed.IsSuccess)
            {
                return Result.Fail<Contracts.V1.ShiftCloseResponse>(closed.Error!);
            }
            await audit.WriteAsync(new AuditEntry(
                UserId: user.UserId, RoleCode: null, StationId: shift.StationId, BranchId: user.BranchId,
                EntitySchema: "sales.shifts", EntityId: shiftId, Action: "SHIFT_CLOSE", Result: "SUCCESS",
                Reason: $"متوقع {expected:0.00} · فعلي {request.CountedCash:0.00} · فارق {variance:0.00}"
                        + (string.IsNullOrWhiteSpace(request.VarianceReasonAr) ? "" : $" — {request.VarianceReasonAr.Trim()}"),
                CorrelationId: correlationId), ct);
            return Result.Ok(new Contracts.V1.ShiftCloseResponse(
                ToDto(closed.Value!.Shift, closed.Value.Reconciliation),
                closed.Value.FinalCount.CountedCash, variance,
                closed.Value.FinalCount.CountedByNameAr, closed.Value.FinalCount.CountedAtUtc));
        }
        catch (FinancePolicyException ex)
        {
            return Result.Fail<Contracts.V1.ShiftCloseResponse>(new(ex.Code, ex.UserMessageAr));
        }
    }

    public async Task<Contracts.V1.ShiftPagedDto> ListAsync(
        long? stationId, string? status, int page, int pageSize, CancellationToken ct = default)
    {
        var (items, total) = await finance.ListShiftsAsync(stationId, status, page, pageSize, ct);
        return new Contracts.V1.ShiftPagedDto(items.Select(ToShiftListItemDto).ToList(), total, page, pageSize);
    }

    public async Task<Result<IReadOnlyList<Contracts.V1.DrawerEventDto>>> ListDrawerEventsAsync(long shiftId, CancellationToken ct = default)
    {
        var shift = await finance.GetShiftAsync(shiftId, ct);
        if (shift is null)
        {
            return Result.Fail<IReadOnlyList<Contracts.V1.DrawerEventDto>>(new("SHIFT_NOT_FOUND", "الوردية غير موجودة"));
        }
        var events = await finance.ListDrawerEventsAsync(shiftId, ct);
        return Result.Ok<IReadOnlyList<Contracts.V1.DrawerEventDto>>(events.Select(e => new Contracts.V1.DrawerEventDto(
            e.EventId, e.EventType, e.Reason, e.RequestedAtUtc, e.RespondedAtUtc, e.ResultStatus, e.TriggeredByNameAr)).ToList());
    }

    public async Task<Result<IReadOnlyList<Contracts.V1.CashCountDto>>> ListCashCountsAsync(long shiftId, CancellationToken ct = default)
    {
        var shift = await finance.GetShiftAsync(shiftId, ct);
        if (shift is null)
        {
            return Result.Fail<IReadOnlyList<Contracts.V1.CashCountDto>>(new("SHIFT_NOT_FOUND", "الوردية غير موجودة"));
        }
        var counts = await finance.ListCashCountsAsync(shiftId, ct);
        return Result.Ok<IReadOnlyList<Contracts.V1.CashCountDto>>(counts.Select(c => new Contracts.V1.CashCountDto(
            c.CountId, c.CountedCash, c.CountedByNameAr, c.CountedAtUtc, c.Notes)).ToList());
    }

    public async Task<Result<Contracts.V1.ShiftCashMovementsDto>> GetCashMovementsAsync(long shiftId, CancellationToken ct = default)
    {
        var shift = await finance.GetShiftAsync(shiftId, ct);
        if (shift is null)
        {
            return Result.Fail<Contracts.V1.ShiftCashMovementsDto>(new("SHIFT_NOT_FOUND", "الوردية غير موجودة"));
        }
        var drawer = await ResolveDrawerAsync(shift, ct);
        if (drawer is null)
        {
            return Result.Fail<Contracts.V1.ShiftCashMovementsDto>(new("DRAWER_ACCOUNT_NOT_FOUND", "لا حساب درج نقد لمحطة هذه الوردية"));
        }
        var ledger = await finance.GetShiftDrawerLedgerAsync(shiftId, drawer.TreasuryAccountId, ct);
        return ledger is null
            ? Result.Fail<Contracts.V1.ShiftCashMovementsDto>(new("SHIFT_NOT_FOUND", "الوردية غير موجودة"))
            : Result.Ok(new Contracts.V1.ShiftCashMovementsDto(
                drawer.TreasuryAccountId, drawer.Code, drawer.NameAr,
                ledger.Movements.Select(t => new Contracts.V1.TreasuryTxnDto(
                    t.TxnId, t.TreasuryAccountId, t.AccountCode, t.AccountNameAr, t.AccountKind,
                    t.Direction, t.Amount, t.SourceDocType, t.SourceDocId, t.ShiftId,
                    t.Reason, t.OccurredAtUtc, t.CreatedByNameAr)).ToList(),
                ledger.NetLedgerMovement, ledger.ExpectedInDrawer));
    }

    /// <summary>تسوية فارق وردية مغلقة بسند مرحّل بمقدار الفارق — الفارق يظل ظاهراً حتى هذه التسوية (بوابة القبول).</summary>
    public async Task<Result<long>> SettleVarianceAsync(
        long shiftId, long voucherId, string? correlationId, CancellationToken ct = default)
    {
        var shift = await finance.GetShiftAsync(shiftId, ct);
        if (shift is null)
        {
            return Result.Fail<long>(new("SHIFT_NOT_FOUND", "الوردية غير موجودة"));
        }
        if (shift.Status != "CLOSED")
        {
            return Result.Fail<long>(new("SHIFT_NOT_CLOSED", "تسوية الفارق لوَردية مغلقة فقط"));
        }
        var variance = shift.Variance ?? 0m;
        if (variance == 0m)
        {
            return Result.Fail<long>(new("SHIFT_NO_VARIANCE", "لا فارق على هذه الوردية — لا تسوية بلا فارق"));
        }
        if (shift.VarianceSettledByVoucherId is not null)
        {
            return Result.Fail<long>(new("SHIFT_ALREADY_SETTLED", "فارق هذه الوردية مسوّى بسند موثق سابقاً"));
        }
        var voucher = await finance.GetVoucherAsync(voucherId, ct);
        if (voucher is null)
        {
            return Result.Fail<long>(new("VOUCHER_NOT_FOUND", "سند التسوية غير موجود"));
        }
        if (voucher.Status != "POSTED")
        {
            return Result.Fail<long>(new("VOUCHER_NOT_POSTED", "سند التسوية يجب أن يكون مرحّلاً — لا ربط بمسودة"));
        }
        if (Math.Abs(voucher.Amount - Math.Abs(variance)) > 0.005m)
        {
            return Result.Fail<long>(new("SETTLEMENT_AMOUNT_MISMATCH",
                $"مبلغ السند ({voucher.Amount:0.00}) لا يطابق مقدار الفارق ({Math.Abs(variance):0.00})"));
        }
        try
        {
            var linked = await finance.LinkVarianceSettlementAsync(shiftId, voucherId, ct);
            if (!linked.IsSuccess)
            {
                return Result.Fail<long>(linked.Error!);
            }
            await audit.WriteAsync(new AuditEntry(
                UserId: user.UserId, RoleCode: null, StationId: shift.StationId, BranchId: shift.BranchId,
                EntitySchema: "sales.shifts", EntityId: shiftId, Action: "SHIFT_VARIANCE_SETTLE", Result: "SUCCESS",
                Reason: $"تسوية فارق {variance:0.00} ج.م بالسند {voucher.VoucherNumber}",
                CorrelationId: correlationId), ct);
            return Result.Ok(shiftId);
        }
        catch (FinancePolicyException ex)
        {
            return Result.Fail<long>(new(ex.Code, ex.UserMessageAr));
        }
    }

    private async Task<TreasuryAccountData?> ResolveDrawerAsync(ShiftListData shift, CancellationToken ct) =>
        // درج المحطة أولاً (الحقيقة الفيزيائية) ثم درج فرعها (توافق خلفي G7) — المنطق في المستودع
        await finance.FindDrawerAccountAsync(shift.StationId, ct);

    internal static Contracts.V1.ShiftListItemDto ToShiftListItemDto(ShiftListData s) => new(
        s.ShiftId, s.ShiftNumber, s.StationId, s.StationCode, s.StationNameAr,
        s.OpenedByDisplayAr, s.OpenedAtUtc, s.OpeningFloat, s.Status,
        s.ClosedAtUtc, s.ClosedByDisplayAr, s.ExpectedCash, s.CountedCash, s.Variance,
        s.VarianceReason, s.VarianceSettledByVoucherId);

    internal static Contracts.V1.ShiftReconciliationDto ToDto(ShiftListData shift, ShiftDrawerLedgerData ledger) => new(
        ToShiftListItemDto(shift),
        ledger.OpeningFloat, ledger.CashSales, ledger.CashRefunds, ledger.DrawerExpenses,
        ledger.VoucherDeposits, ledger.VoucherWithdrawals, ledger.TransfersIn, ledger.TransfersOut,
        ledger.ExpectedInDrawer,
        shift.CountedCash, shift.Variance, shift.VarianceReason, shift.VarianceSettledByVoucherId,
        ledger.CardCollected, ledger.CreditIssued, ledger.InvoiceCount);
}

/// <summary>
/// طباعة مستندات G8 (§29.10): السندات والمصروفات صريحة على طابعة المالية، والملخصات على
/// رول المحطة، وكشوف الرواتب على الطابعة المؤمنة ONLY — لا طابعة عامة أبداً (اختبار 21).
/// </summary>
public sealed class PrintFinanceDocumentsUseCase(IFinanceRepository finance, IAuditWriter audit, ICurrentUser user)
{
    public async Task<Result<Contracts.V1.FinancePrintResponse>> PrintAsync(
        Contracts.V1.PrintFinanceDocumentRequest request, string? correlationId, CancellationToken ct = default)
    {
        var documentTypes = FinancePrintPolicy.ConfidentialDocumentTypes
            .Concat(FinancePrintPolicy.FinanceVoucherDocumentTypes)
            .Concat(FinancePrintPolicy.StationRollSummaryDocumentTypes)
            .ToHashSet(StringComparer.Ordinal);
        if (!documentTypes.Contains(request.DocumentType))
        {
            return Result.Fail<Contracts.V1.FinancePrintResponse>(new("DOCUMENT_TYPE_NOT_PRINTABLE",
                "نوع المستند ليس من مستندات طباعة G8 (السندات/المصروفات/الرواتب/الملخصات)"));
        }
        if (user.StationId is not { } stationId)
        {
            return Result.Fail<Contracts.V1.FinancePrintResponse>(new("STATION_REQUIRED",
                "الطباعة تتم من جلسة محطة — لتوجيه المستند إلى طابعة دورها المعتمد"));
        }

        // خصوصية الرواتب (§31.1/§35.2 — اختبار 21): قرارات التوجيه من سياسة المجال الصرفة،
        // ومستند الرواتب بلا طابعة مؤمنة لا يوجَّه لغيرها إطلاقاً — الإشعار صادق والمستند محفوظ.
        var confidentialPrinter = await finance.GetStationRolePrinterAsync(stationId, FinancePrintPolicy.ConfidentialRole, ct);
        var financePrinter = await finance.GetStationRolePrinterAsync(stationId, FinancePrintPolicy.FinanceVouchersRole, ct);
        var receiptPrinter = await finance.GetStationRolePrinterAsync(stationId, FinancePrintPolicy.CustomerReceiptRole, ct);
        var routing = FinancePrintPolicy.Route(
            request.DocumentType, confidentialPrinter is not null, financePrinter is not null, receiptPrinter is not null);
        if (!routing.Allowed)
        {
            await audit.WriteAsync(new AuditEntry(
                UserId: user.UserId, RoleCode: null, StationId: stationId, BranchId: user.BranchId,
                EntitySchema: "print.print_jobs", EntityId: request.DocumentId, Action: "FINANCE_PRINT_DENIED", Result: "DENIED",
                Reason: $"{request.DocumentType} — {routing.DenyCode}", CorrelationId: correlationId), ct);
            return Result.Fail<Contracts.V1.FinancePrintResponse>(new(routing.DenyCode!, routing.DenyMessageAr!));
        }

        try
        {
            var outcome = await finance.CreateFinancePrintJobAsync(
                stationId, user.UserId, request.DocumentType, request.DocumentId,
                request.ReprintOfJobId is null ? "ORIGINAL" : "REPRINT", request.ReprintOfJobId, ct);
            // نتيجة التدقيق من قائمة V008 (SUCCESS/DENIED/FAILED): رفضُ الخصوصية DENIED وفقدان الطابعة FAILED
            var auditResult = outcome.JobCreated ? "SUCCESS" : "FAILED";
            await audit.WriteAsync(new AuditEntry(
                UserId: user.UserId, RoleCode: null, StationId: stationId, BranchId: user.BranchId,
                EntitySchema: "print.print_jobs", EntityId: outcome.JobId ?? request.DocumentId,
                Action: request.ReprintOfJobId is null ? "FINANCE_PRINT" : "FINANCE_REPRINT",
                Result: auditResult,
                Reason: $"{request.DocumentType}#{request.DocumentId} على دور {routing.TargetRole} — {outcome.JobStatus ?? outcome.NoticeAr}",
                CorrelationId: correlationId), ct);
            return Result.Ok(new Contracts.V1.FinancePrintResponse(
                outcome.JobCreated, outcome.JobId, outcome.JobStatus, outcome.MediaClass,
                outcome.HoldReasonCode, outcome.NoticeAr, routing.TargetRole,
                FinancePrintPolicy.RequiresSummaryBadge(request.DocumentType)));
        }
        catch (FinancePolicyException ex)
        {
            return Result.Fail<Contracts.V1.FinancePrintResponse>(new(ex.Code, ex.UserMessageAr));
        }
    }
}
