using System.ComponentModel.DataAnnotations;
using LakeCountrySpanish.Web.Models.Entities;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace LakeCountrySpanish.Web.Models.ViewModels;

/// <summary>
/// The parent-facing enrollment form model. Bound from the POST /join/{slug}
/// submission. Content validation (StringLength, EmailAddress, etc.) is
/// enforced by model-binding data annotations; presence of Student /
/// Emergency / Pickup fields is enforced conditionally via
/// <see cref="IValidatableObject.Validate"/> — required for kid programs
/// (the parent-child flow), skipped for adult programs where the enrollee
/// IS the student and there's no pickup/emergency-contact model.
/// </summary>
public sealed class ProgramEnrollmentFormViewModel : IValidatableObject
{
    // Populated by the controller from the URL slug — parent doesn't fill it.
    public int ProgramId { get; set; }
    public string ProgramSlug { get; set; } = string.Empty;

    /// <summary>
    /// Populated by the controller from the URL slug (both GET render and POST
    /// re-render on validation failure). <see cref="BindNeverAttribute"/> keeps
    /// the model binder from trying to bind form values to it, and
    /// <see cref="ValidationNeverAttribute"/> opts out of ASP.NET Core's
    /// implicit-required-for-non-nullable-reference-types rule that would
    /// otherwise silently fail validation on every POST.
    /// </summary>
    [BindNever, ValidateNever]
    public EnrollmentProgram Program { get; set; } = null!;

    /// <summary>
    /// Server-set flag (never bound from client input to avoid a hostile
    /// client bypassing student-required validation by lying about the
    /// program). Controller sets from <c>Program.AgeMin &gt;= 18</c>. Drives
    /// both the conditional validation in <see cref="Validate"/> and the
    /// view's conditional rendering of Student / Pickup / Emergency sections.
    /// </summary>
    [BindNever]
    public bool IsAdultProgram { get; set; }

    /// <summary>
    /// Honeypot field — hidden from real users via CSS + aria-hidden +
    /// tabindex=-1 + autocomplete=off. Bots fill every input they see;
    /// humans never see this one. Any non-empty submit is silently dropped
    /// by the controller (redirect to the same page with a success-looking
    /// message, per Mark's LearnedGeek pattern — don't tip off the bot).
    /// Mirrors the LearnedGeek contact-form field naming ("Website") that
    /// spammers reliably fill because URL-fill logic keys on that label.
    /// </summary>
    public string? Website { get; set; }

    // ---------------- Parent (or self, for adult programs) ----------------

    [Required, StringLength(80)]
    [Display(Name = "First name")]
    public string ParentFirstName { get; set; } = string.Empty;

    [Required, StringLength(80)]
    [Display(Name = "Last name")]
    public string ParentLastName { get; set; } = string.Empty;

    [Required, EmailAddress, StringLength(200)]
    [Display(Name = "Email")]
    public string ParentEmail { get; set; } = string.Empty;

    [Required, StringLength(20)]
    [Display(Name = "Phone")]
    public string ParentPhone { get; set; } = string.Empty;

    [Required, StringLength(200)]
    [Display(Name = "Street address")]
    public string ParentAddressLine1 { get; set; } = string.Empty;

    [Required, StringLength(80)]
    [Display(Name = "City")]
    public string ParentCity { get; set; } = string.Empty;

    [Required, StringLength(20)]
    [Display(Name = "State")]
    public string ParentState { get; set; } = "WI";

    [Required, StringLength(20)]
    [Display(Name = "ZIP")]
    public string ParentZip { get; set; } = string.Empty;

    // ---------------- Student ----------------
    // Nullable + no [Required]. Presence is enforced in Validate() only for
    // kid programs. For adult programs, the controller auto-fills these
    // from the Parent fields on save so the DB row stays consistent (emails
    // and admin views read StudentFirstName/LastName).

    [StringLength(80)]
    [Display(Name = "Student first name")]
    public string? StudentFirstName { get; set; }

    [StringLength(80)]
    [Display(Name = "Student last name")]
    public string? StudentLastName { get; set; }

    [StringLength(20)]
    [Display(Name = "Grade")]
    public string? StudentGrade { get; set; }

    [DataType(DataType.Date)]
    [Display(Name = "Date of birth")]
    public DateOnly? StudentBirthDate { get; set; }

    [StringLength(1000)]
    [Display(Name = "Medical concerns (allergies, medications, conditions, sensory)")]
    public string? MedicalConcerns { get; set; }

    [StringLength(1000)]
    [Display(Name = "Anything else we should know")]
    public string? StudentNotes { get; set; }

    // ---------------- Emergency ----------------
    // Required for kid programs only.

    [StringLength(160)]
    [Display(Name = "Emergency contact name")]
    public string? EmergencyName { get; set; }

    [StringLength(20)]
    [Display(Name = "Emergency contact phone")]
    public string? EmergencyPhone { get; set; }

    [StringLength(60)]
    [Display(Name = "Relationship to student")]
    public string? EmergencyRelationship { get; set; }

    // ---------------- Pickup ----------------
    // Required for kid programs only; hidden entirely for adults.

    [StringLength(500)]
    [Display(Name = "Authorized pickup names",
             Description = "Who is allowed to pick up your child? One per line or comma-separated. Include their relationship (parent, grandparent, aunt, etc).")]
    public string? PickupAuthorization { get; set; }

    // ---------------- Payment ----------------

    [Required]
    [Display(Name = "Payment option")]
    public ProgramPaymentType PaymentType { get; set; } = ProgramPaymentType.FullOneTime;

    // ---------------- Consent ----------------

    [Range(typeof(bool), "true", "true", ErrorMessage = "You must accept the waiver to enroll.")]
    [Display(Name = "I have read and accept the waiver")]
    public bool WaiverAccepted { get; set; }

    // Label swapped at view-time based on IsAdultProgram — parent sees
    // "my child" wording, adult sees "me" wording. Both send the same bool.
    [Display(Name = "I grant Lake Country Spanish permission to photograph or video my child during the program")]
    public bool PhotoReleaseGranted { get; set; }

    /// <summary>
    /// Presence-check conditional on <see cref="IsAdultProgram"/>. Content
    /// validation (StringLength, EmailAddress) still runs regardless via the
    /// data annotations — this only enforces that fields are non-empty when
    /// the program is a kid program.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (IsAdultProgram) yield break;

        if (string.IsNullOrWhiteSpace(StudentFirstName))
            yield return new ValidationResult("Student first name is required.", new[] { nameof(StudentFirstName) });
        if (string.IsNullOrWhiteSpace(StudentLastName))
            yield return new ValidationResult("Student last name is required.", new[] { nameof(StudentLastName) });
        if (string.IsNullOrWhiteSpace(StudentGrade))
            yield return new ValidationResult("Grade is required.", new[] { nameof(StudentGrade) });
        if (!StudentBirthDate.HasValue)
            yield return new ValidationResult("Date of birth is required.", new[] { nameof(StudentBirthDate) });
        if (string.IsNullOrWhiteSpace(EmergencyName))
            yield return new ValidationResult("Emergency contact name is required.", new[] { nameof(EmergencyName) });
        if (string.IsNullOrWhiteSpace(EmergencyPhone))
            yield return new ValidationResult("Emergency contact phone is required.", new[] { nameof(EmergencyPhone) });
        if (string.IsNullOrWhiteSpace(EmergencyRelationship))
            yield return new ValidationResult("Relationship to student is required.", new[] { nameof(EmergencyRelationship) });
        if (string.IsNullOrWhiteSpace(PickupAuthorization))
            yield return new ValidationResult("Authorized pickup names is required.", new[] { nameof(PickupAuthorization) });
    }
}

/// <summary>Thank-you page after a successful enrollment (any payment path).</summary>
public sealed class ProgramEnrollmentThankYouViewModel
{
    public EnrollmentProgram Program { get; init; } = null!;
    public ProgramEnrollment Enrollment { get; init; } = null!;

    /// <summary>Display label for the payment state, tailored to the payment type.</summary>
    public string PaymentStatusHeadline => (Enrollment.PaymentType, Enrollment.Status) switch
    {
        (ProgramPaymentType.FullOneTime, ProgramEnrollmentStatus.FullyPaid) =>
            "Payment received — you're all set!",

        (ProgramPaymentType.TwoInstallment, ProgramEnrollmentStatus.FirstPaymentComplete) =>
            "First installment received — you're enrolled!",

        (ProgramPaymentType.TwoInstallment, ProgramEnrollmentStatus.FullyPaid) =>
            "Both installments received — you're all set!",

        (ProgramPaymentType.CashInHand, _) =>
            "Registration received — please bring your payment to the booth to confirm.",

        _ => "Registration received. Your payment is still processing — you'll get an email once it clears."
    };
}
