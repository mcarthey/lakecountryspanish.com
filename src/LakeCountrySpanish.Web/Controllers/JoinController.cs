using LakeCountrySpanish.Web.Models.Entities;
using LakeCountrySpanish.Web.Models.ViewModels;
using LakeCountrySpanish.Web.Services.Programs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LakeCountrySpanish.Web.Controllers;

/// <summary>
/// Public parent-facing enrollment landing pages at <c>/join/{slug}</c>. Fully
/// anonymous — no LCS account required. Each Program's <c>Slug</c> is what
/// Karen encodes into the QR code she prints for the booth.
/// </summary>
[AllowAnonymous]
[Route("join")]
public class JoinController : Controller
{
    private readonly IEnrollmentProgramService _programs;
    private readonly IProgramEnrollmentService _enrollments;
    private readonly ILogger<JoinController> _logger;

    public JoinController(
        IEnrollmentProgramService programs,
        IProgramEnrollmentService enrollments,
        ILogger<JoinController> logger)
    {
        _programs = programs;
        _enrollments = enrollments;
        _logger = logger;
    }

    /// <summary>Landing page: hero, program summary, enrollment form.</summary>
    [HttpGet("{slug}")]
    public async Task<IActionResult> Landing(string slug, bool cancelled = false, CancellationToken ct = default)
    {
        var program = await _programs.GetBySlugAsync(slug, ct);
        if (program is null || !program.IsActive) return NotFound();

        // Hard-block enrollment before the start date (Karen may pre-list a
        // program for parents to see, but only accept signups after a certain
        // date — e.g. "opens Sep 1").
        if (program.IsEnrollmentNotYetOpen)
        {
            TempData["InfoMessage"] = $"Enrollment for \"{program.Name}\" opens on {program.EnrollmentStartsAt!.Value:MMMM d, yyyy}. Check back then to sign up.";
            return RedirectToAction("Detail", "Programs", new { slug = program.Slug });
        }

        // Hard-block enrollment after the deadline (or after the program has
        // already started, if no explicit deadline was set). Redirects to the
        // public /programs listing so the parent lands somewhere useful
        // instead of a wall.
        if (program.IsEnrollmentClosed)
        {
            TempData["InfoMessage"] = $"Enrollment for \"{program.Name}\" has closed. Reach out via the contact form if you'd like to be notified about the next session.";
            return RedirectToAction("Index", "Programs");
        }

        if (cancelled)
        {
            TempData["InfoMessage"] = "Payment was cancelled — your enrollment wasn't saved. Try again below.";
        }

        var vm = new ProgramEnrollmentFormViewModel
        {
            ProgramId = program.Id,
            ProgramSlug = program.Slug,
            Program = program,
            ParentState = "WI",
            // Adult programs skip the parent-child fields. Karen's convention:
            // AgeMin >= 18 flags an adult program. If she adds a mixed-audience
            // program in the future, we'd want an explicit IsAdultProgram flag
            // on the entity instead of inferring from age.
            IsAdultProgram = program.AgeMin >= 18,
            PaymentType = program.InstallmentsEnabled
                ? ProgramPaymentType.FullOneTime  // default to full even when installments offered
                : ProgramPaymentType.FullOneTime
        };
        return View(vm);
    }

    /// <summary>
    /// Form submission: save enrollment + kick off Stripe (or short-circuit
    /// for cash). Protected by:
    /// <list type="bullet">
    ///   <item>Honeypot — <see cref="ProgramEnrollmentFormViewModel.Website"/>
    ///   is invisible to real users; any non-empty submit is silently
    ///   dropped (redirect to same page with a success-looking message so
    ///   the bot doesn't learn it's been blocked).</item>
    ///   <item>Rate limiter — the "enrollment-submit" policy in
    ///   <c>Program.cs</c> caps IP-scoped submissions per hour.</item>
    /// </list>
    /// Both added 2026-09-06 after a scripted-enrollment attack.
    /// </summary>
    [HttpPost("{slug}")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("enrollment-submit")]
    public async Task<IActionResult> Submit(string slug, ProgramEnrollmentFormViewModel model, CancellationToken ct)
    {
        // Honeypot: silent-success pattern. Bot never learns it was rejected.
        if (!string.IsNullOrWhiteSpace(model.Website))
        {
            _logger.LogWarning("Enrollment honeypot triggered on {Slug} from {Ip} (UA: {UA})",
                slug,
                HttpContext.Connection.RemoteIpAddress,
                Request.Headers.UserAgent.ToString());
            TempData["InfoMessage"] = "Registration received — check your inbox for next steps.";
            return RedirectToAction(nameof(Landing), new { slug });
        }

        var program = await _programs.GetBySlugAsync(slug, ct);
        if (program is null || !program.IsActive) return NotFound();

        // Race-condition guard: someone might have loaded the form pre-deadline
        // (or pre-open) and submitted after or before. Same short-circuits as
        // the GET action.
        if (program.IsEnrollmentNotYetOpen)
        {
            TempData["InfoMessage"] = $"Enrollment for \"{program.Name}\" opens on {program.EnrollmentStartsAt!.Value:MMMM d, yyyy}. Try again then.";
            return RedirectToAction("Detail", "Programs", new { slug = program.Slug });
        }
        if (program.IsEnrollmentClosed)
        {
            TempData["InfoMessage"] = $"Enrollment for \"{program.Name}\" closed while you were filling out the form. Reach out via the contact form if you'd like to be notified about the next session.";
            return RedirectToAction("Index", "Programs");
        }

        // Repopulate the Program-linked fields so the form can re-render on validation failure.
        model.ProgramId = program.Id;
        model.ProgramSlug = program.Slug;
        model.Program = program;
        model.IsAdultProgram = program.AgeMin >= 18;

        // Re-run validation with the server-set IsAdultProgram flag in place
        // so Validate() applies the right rules for the mode. Model binding
        // happened before we could set IsAdultProgram; clear + re-validate.
        ModelState.Clear();
        TryValidateModel(model);

        if (!ModelState.IsValid) return View("Landing", model);

        // For adult programs the enrollee IS the student. Mirror parent name
        // into student name so downstream code (emails, admin views) that
        // reads StudentFirstName/LastName still has something to render.
        if (model.IsAdultProgram)
        {
            model.StudentFirstName = model.ParentFirstName;
            model.StudentLastName = model.ParentLastName;
        }

        var submission = new EnrollmentSubmission
        {
            ProgramId = program.Id,
            PaymentType = model.PaymentType,
            ParentFirstName = model.ParentFirstName,
            ParentLastName = model.ParentLastName,
            ParentEmail = model.ParentEmail,
            ParentPhone = model.ParentPhone,
            ParentAddressLine1 = model.ParentAddressLine1,
            ParentCity = model.ParentCity,
            ParentState = model.ParentState,
            ParentZip = model.ParentZip,
            StudentFirstName = model.StudentFirstName ?? string.Empty,
            StudentLastName = model.StudentLastName ?? string.Empty,
            StudentGrade = model.StudentGrade ?? string.Empty,
            StudentBirthDate = model.StudentBirthDate ?? default,
            MedicalConcerns = model.MedicalConcerns,
            StudentNotes = model.StudentNotes,
            EmergencyName = model.EmergencyName ?? string.Empty,
            EmergencyPhone = model.EmergencyPhone ?? string.Empty,
            EmergencyRelationship = model.EmergencyRelationship ?? string.Empty,
            PickupAuthorization = model.PickupAuthorization ?? string.Empty,
            WaiverAccepted = model.WaiverAccepted,
            PhotoReleaseGranted = model.PhotoReleaseGranted,
            BaseUrl = $"{Request.Scheme}://{Request.Host}"
        };

        var result = await _enrollments.SubmitAsync(submission, ct);

        if (!result.Success)
        {
            ModelState.AddModelError(string.Empty, result.ErrorMessage ?? "Something went wrong. Please try again.");
            return View("Landing", model);
        }

        if (result.IsCashInHand)
        {
            return RedirectToAction(nameof(ThankYou), new { slug, id = result.EnrollmentId });
        }

        // Stripe path — redirect to the hosted Checkout URL.
        return Redirect(result.StripeCheckoutUrl!);
    }

    /// <summary>
    /// Thank-you landing after a successful Stripe checkout or cash-in-hand
    /// submission. Two lookup paths: Stripe session id (from success_url) or
    /// enrollment id (from cash path). Whichever is present wins.
    /// </summary>
    [HttpGet("{slug}/thank-you")]
    public async Task<IActionResult> ThankYou(string slug, string? session = null, int? id = null, CancellationToken ct = default)
    {
        var program = await _programs.GetBySlugAsync(slug, ct);
        if (program is null) return NotFound();

        ProgramEnrollment? enrollment = null;
        if (!string.IsNullOrEmpty(session))
        {
            enrollment = await _enrollments.GetByCheckoutSessionAsync(session, ct);
        }
        else if (id.HasValue)
        {
            enrollment = await _enrollments.GetAsync(id.Value, ct);
        }

        if (enrollment is null || enrollment.ProgramId != program.Id)
        {
            _logger.LogWarning("Thank-you page hit for slug {Slug} but no matching enrollment (session={Session}, id={Id})", slug, session, id);
            return NotFound();
        }

        return View(new ProgramEnrollmentThankYouViewModel
        {
            Program = program,
            Enrollment = enrollment
        });
    }
}
