# Logic Guide

I am currently documenting the minimum requirements to achieve room randomization. This is for two reasons: (1) room rando is a cool future concept, and (2) it makes validating and maintaining the logic really straightforward.

It involves going room by room and documenting the minimum requirements to reach each relevant part of the room, **without leaving and reentering**. Each room should document: every room exit, the destination room and corresponding destination exit, each check or potentially randomized object, and the traversal requirements needed to reach each of those. 

Some rooms are really complex with lots of checks, have platforming challenges (gap too wide to jump without dash/silk soar spot), or unlock criteria (gate/breakable wall/etc.). So to simplify the notation, you can define "subrooms" and the logic required to reach one subroom from another subroom. You can also specify which subroom an exit or check is in.

My current notes define these per room:
**Room Comments**
Freeform text with thoughts/challenges/concerns about the room

**Room Exits**
A room exit is any loading zone that causes the current game scene to transition into another room. Each exit must identify a simple identifier, the destination room, the corresponding destination exit, the subroom containing the exit (if applicable).

**Subrooms**
Subrooms are optional logical divisions within a room. Use them when different parts of a room are separated by a platforming requirement (movement tech) or unlock requirement (gates controlled by switches, breakable walls, etc.)

**Subroom Connections**
A subroom connection defines one way of moving from one subroom to another. Each traversable direction must be documented independently because the requirements may be different.

For example:
> Subroom A -> Subroom B: requires faydown cloak
> Subroom B -> Subroom A: none (fall)
or 
> Subroom A -> Subroom B: gate must be unlocked
> Subroom B -> Subroom A: none (gate switch is on this side)

**Checks/State/Things you thought were kinda cool**
Any checks or interactable objects that could be randomized in the future (beast shards, switches, benches, paygates, rosary dishes, etc.). 

**Requirements**
Requirements need to be as detailed as possible and cover *any possible scenario*. For example, it isn't enough to specify "run OR dash" if faydown cloak would also get you the requirement. I have this reminder posted at the bottom of my app as a sort of quick movement tech checklist:
`remember your options: spike pogo, crest pogo, run, dash, drifter's cloak, faydown cloak, cling grip, silk soar, clawline, sharpdart`

Requirements may also be managed by room state, rather than movement abilities: requires gate unlocked by switch on other side
list of room exits.
