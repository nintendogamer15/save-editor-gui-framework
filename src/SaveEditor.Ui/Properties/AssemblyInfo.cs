using System.Runtime.CompilerServices;

// Some rules that decide a refusal cannot be reached through a fixture: a cloud placeholder
// cannot be fabricated in a test, and a mapped network drive cannot be assumed to exist on a
// build agent. Those rules are unit-tested directly instead.
//
// They stay internal rather than becoming public API. The public surface freezes at 1.0 under
// major-version discipline, and a reparse-tag bit test is an implementation detail no consumer
// should ever bind to — widening the contract to reach a test is the wrong trade.
[assembly: InternalsVisibleTo("SaveEditor.Ui.Tests")]
[assembly: InternalsVisibleTo("SaveEditor.Ui.HeadlessTests")]

