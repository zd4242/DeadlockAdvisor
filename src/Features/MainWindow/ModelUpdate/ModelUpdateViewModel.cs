using System.Reactive;
using DeadlockAdvisor.Core;
using DeadlockAdvisor.Services;
using DeadlockAdvisor.Services.Contracts;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace DeadlockAdvisor.Features.MainWindow.ModelUpdate;

/// <summary>One file changed here: replace it with the published version, or keep it.</summary>
public sealed class ModelFileChoice(string file) : ReactiveObject
{
    public string File { get; } = file;

    /// <summary>"Hero trait ratings".</summary>
    public string Title { get; } = ModelManifest.Title(file);

    [Reactive] public bool Replace { get; set; }
}

/// <summary>
/// The published model against files changed here, each kept unless ticked: for a newer version
/// (<see cref="Update"/>), with the files nobody changed updated along with them, or to put files back to
/// the published version (<see cref="Reset"/>).
/// </summary>
public sealed class ModelUpdateViewModel : ViewModelBase
{
    private ModelUpdateViewModel(IModalService modals, ModelUpdatePlan plan, Action<IReadOnlySet<string>> apply, string title, string intro,
        string question, string confirmText, string footer, bool ticked)
    {
        Title = title;
        Intro = intro;
        Question = question;
        ConfirmText = confirmText;
        Footer = footer;
        News = ticked ? [] : plan.News.Select(note => $"{note.Published}: {note.Text}").ToList();
        Choices = plan.Edited.Select(file => new ModelFileChoice(file) { Replace = ticked }).ToList();
        AlsoUpdated = plan.Quiet.Count == 0
            ? ""
            : "Also updated, as you haven't changed them: " + string.Join(", ", plan.Quiet.Select(ModelManifest.Title)) + ".";

        UpdateCommand = ReactiveCommand.Create(() =>
        {
            modals.CloseModal();
            apply(Choices.Where(choice => choice.Replace).Select(choice => choice.File).ToHashSet());
        });
        NotNowCommand = ReactiveCommand.Create(modals.CloseModal);
    }

    /// <summary>A newer version, when files it replaces were changed here: those are kept unless ticked.</summary>
    /// <param name="apply">Called with the edited files to replace; the rest of them are kept.</param>
    public static ModelUpdateViewModel Update(IModalService modals, ModelUpdatePlan update, Action<IReadOnlySet<string>> apply) =>
        new(modals, update, apply, "Formula update",
            $"A newer version of the hero ratings and item formulas was published on {update.Published.Published}. "
            + "You've changed some of the files it replaces since they were installed, by editing them or with Sync from Game API.",
            "REPLACE YOUR CHANGES?", "Update",
            "Unticked files keep your changes, and this version won't ask about them again (Data → Check for Formula Updates does). "
            + "Every replaced file keeps a backup in data\\.backups.",
            ticked: false);

    /// <summary>Every file that differs from the published version, ticked, to put back to it.</summary>
    /// <param name="apply">Called with the files to put back; the rest are left as they are.</param>
    public static ModelUpdateViewModel Reset(IModalService modals, ModelUpdatePlan reset, Action<IReadOnlySet<string>> apply) =>
        new(modals, reset, apply, "Reset formulas",
            $"These files differ from the version published on {reset.Published.Published}: edited, synced from the game API, "
            + "or kept over an update. Ticked ones go back to the published version.",
            "PUT BACK TO THE PUBLISHED VERSION", "Reset",
            "Every replaced file keeps a backup in data\\.backups, and Settings → Data can undo the reset.",
            ticked: true);

    public string Title { get; }

    public string Intro { get; }

    /// <summary>"2026-10-09: Spirit items rate higher against Haze.": what the publisher said changed since your version, newest first.</summary>
    public IReadOnlyList<string> News { get; }

    public bool HasNews => News.Count > 0;

    /// <summary>The heading over the files to tick.</summary>
    public string Question { get; }

    /// <summary>Tick one to replace it with the published version.</summary>
    public IReadOnlyList<ModelFileChoice> Choices { get; }

    public string AlsoUpdated { get; }

    public bool HasAlsoUpdated => AlsoUpdated.Length > 0;

    public string Footer { get; }

    public string ConfirmText { get; }

    public ReactiveCommand<Unit, Unit> UpdateCommand { get; }
    public ReactiveCommand<Unit, Unit> NotNowCommand { get; }
}