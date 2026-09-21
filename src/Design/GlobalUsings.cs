// The one namespace this project's own files see everywhere — brief-lvs-2-shared-extraction.md
// R-lvs2-1.
//
// CircuitRF.Design.Layout.Extraction is where the copper reading lives: the partition, the placed
// pins, the galvanic walk and the conductor enumeration. Four folders read it — Layout/Pdn,
// Layout/Drc, Layout/Interchange and RailRf — and brief 3 adds a fifth. Listing it once is
// src/Ui/GlobalUsings.cs' own arrangement, for its reason.

global using CircuitRF.Design.Layout.Extraction;
