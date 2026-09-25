# Project Context
This is a dotnet project using Avalonia and Reactive UI, a C# port of the PyQt6 Deadlock Item Advisor (`D:\Dev\Python\deadlock_advisor`), which recommends in-game items based on the heroes in your match. The port plan is `D:\Dev\Python\deadlock_advisor\docs\csharp_port_plan.md`; the Python code is the source of truth for behaviour.

# Guidelines
- Prefer solutions that follow good practices and result in cleaner code, over whatever the simplest solution is.
- Ideally focus on solving the root issue of a problem rather than just treating the symptoms.
- When it makes sense to (e.g. doesn't overly complicate something simple), refactor logic to be shared instead of duplicating it.
- For `if` statements with a single statement afterwards, move that statement to the following line instead of on a single line.
- When debugging a bug, consider if any debug statements (and their output) would help you better understand what is not working correctly.
- Only leave comments if they are concise and explain a somewhat complicated or not immediately apparent section of code, and don't leave comments if it is only relevant to explain or highlight the recent set of changes that were made.
- The global usings file contains the following:

```csharp
global using Avalonia;
global using Avalonia.Controls;
global using System;
global using System.Collections.Generic;
global using System.Linq;
global using System.Threading.Tasks;
```
