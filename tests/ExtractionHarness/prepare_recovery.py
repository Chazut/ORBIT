"""Compile the current MovementSystem recovery methods against the harness engine doubles."""
from pathlib import Path
import re
import sys

root = Path(__file__).resolve().parents[2]
source = (root / "Orbit/Systems/MovementSystem.cs").read_text(encoding="utf-8-sig")
members = []
for name in (
    "IdleRescueNoMoveRadiusSqr", "IdleRescueThresholdSeconds", "IdleRescueRingRadii",
    "TryIdleIslandRescue", "TryLocalRescuePoint", "IsRescueDestinationHidden",
    "ResetAfterRescue", "ResumeAfterRescue", "CompleteLocalRescue", "ResumeGroundPlacement",
    "RescueTeleportToConnectedPoint", "RescueTeleportNearSquadmate", "StopExfilApproach", "StopAtCorpse",
    "TryFollowPreparedPath", "StartMovement",
):
    # A field/expression ends on the declaration line. A block ends at class-member indentation.
    declarations = list(re.finditer(r"^    (?:private|internal) [^\n]*?\b" + name +
                                    r"(?=[ (=])[^\n]*$", source, re.M))
    if len(declarations) != 1:
        raise ValueError(f"Expected one MovementSystem declaration for {name}, found {len(declarations)}")
    decl = declarations[0]
    end = decl.end()
    if ";" not in decl.group():
        closing = re.search(r"^    \}$", source[end:], re.M)
        if not closing:
            raise ValueError(f"Missing end of {name}")
        end += closing.end()
    members.append(source[decl.start():end])

output = Path(sys.argv[1])
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text("using Orbit.Entities;\nusing Orbit.Navigation;\nusing UnityEngine;\n"
                  "using UnityEngine.AI;\nnamespace Orbit.Systems;\npublic partial class MovementSystem\n{\n"
                  + "\n\n".join(members) + "\n}\n", encoding="utf-8")
