# Resources & Bibliography

Every piece of mathematics, every algorithm, every UI affordance in Fracturing Fog grew out of work
done by someone else. This page collects the references that informed the project — books, papers,
canonical websites, GitHub repositories, library docs, and useful explainers. Where the application
ships a particular algorithm or formula, you should be able to trace it from the relevant doc page
back to a citation here.

> **Source citation contract.** Fracturing Fog lives on publicly available work by the mathematics
> and fractal-rendering community. Crediting that work is a standing obligation, not a courtesy:
> every source the code or docs lean on is credited **here**, in the same change that introduces
> it, and no credit is ever removed. The rule, and the CI guard that enforces it
> (`Tools/CitationGuard/citation_guard.py`), are in
> [CONTRIBUTING.md → Source citation contract](../CONTRIBUTING.md#source-citation-contract).

If you spot a missing reference, file an issue or open a pull request. Citations age — broken links
are flagged for fixing in [Documentation Plan → Citation maintenance](Documentation-Plan.md#citation-maintenance).
Entries whose publication details are not yet confirmed are kept, not dropped — see
[Citations awaiting verification](#citations-awaiting-verification). Corrections keep the old form
on record — see [Corrected attributions](#corrected-attributions).

---

## Foundational mathematics

### Fractal geometry and complex dynamics

- **Benoit B. Mandelbrot.** *The Fractal Geometry of Nature.* W. H. Freeman, 1982. The book that
  coined the word "fractal" and gave the Mandelbrot set its first wide audience.
- **Adrien Douady, John H. Hubbard.** *Étude dynamique des polynômes complexes.* Publications
  mathématiques d'Orsay, 1984/1985. Proved the Mandelbrot set is connected; introduced the
  parameter-ray theory used to label hyperbolic components, and the external rays, landing theorems
  and tuning the location finder relies on.
- **Heinz-Otto Peitgen, Peter H. Richter.** *The Beauty of Fractals: Images of Complex Dynamical
  Systems.* Springer-Verlag, 1986. The earliest accessible visual catalogue.
- **Heinz-Otto Peitgen, Hartmut Jürgens, Dietmar Saupe.** *Chaos and Fractals: New Frontiers of
  Science.* Springer, 1992 (and their 1980s Newton-basin imagery). Cited by the Newton help text for
  the first drawings of the Newton-basin boundary.
- **John W. Milnor.** *Dynamics in One Complex Variable.* Princeton University Press, 3rd ed., 2006.
  The modern standard reference for iterating rational maps on the Riemann sphere.
- **John W. Milnor.** *Cubic polynomial maps with periodic critical orbit.* arXiv:0910.1866, 2009.
  Escape regions of the two-critical-point family (dual-orbit research).
- **Robert L. Devaney.** *An Introduction to Chaotic Dynamical Systems.* Addison-Wesley (2nd ed.
  1989; later editions CRC Press). The exponential family and broader chaos background.
- **Wolf Jung.** *Mandel — Software for Complex Dynamics.* <https://mndynamics.com/indexp.html>.
  Reference tool for orbit-portrait and external-ray verification.

### Specific fractal families

- **Pierre Fatou, Gaston Julia.** *Sur les équations fonctionnelles.* Bull. Soc. Math. France, 1917-1919.
  The origins of complex iteration theory; the Julia sets are named here.
- **Arthur Cayley.** *The Newton–Fourier imaginary problem.* American Journal of Mathematics 2, 1879.
  Newton's method in the complex plane; the d = 2 case behind the Newton family's history notes.
- **Edmond Halley.** *Methodus nova accurata & facilis inveniendi radices aequationum quarumcumque.*
  Philosophical Transactions of the Royal Society 18, 1694. The cubically convergent root-finder
  behind the Halley fractal.
- **Paul Derbyshire.** *Nova fractal*, 1990s (Fractint / sci.fractals community). Newton's iteration
  plus a `+c` term — the Nova family (#1294).
- **Michael F. Barnsley.** *Fractals Everywhere.* Academic Press, 2nd ed., 1993. Defines the chaos
  game and the iterated function systems used by the IFS renderer.
- **John Hutchinson.** *Fractals and self similarity.* Indiana University Mathematics Journal, 1981.
  Original IFS attractor theorem.
- **Shigehiro Ushiki.** *Phoenix.* IEEE Transactions on Circuits and Systems, 1988. The two-step
  memory recurrence used by the Phoenix family.
- **Michael Michelitsch, Otto E. Rössler.** *A New Feature in the Mandelbrot Set.* Computers & Graphics,
  1992. Defines the Burning Ship.
- **W. D. Crowe, R. Hasson, P. J. Rippon, P. E. D. Strain-Clark.** *On the structure of the Mandelbar
  set.* Nonlinearity 2(4), 1989. The Tricorn / Mandelbar (`conj(z)² + c`).
- **Daniel White.** *Triplex algebra and the Mandelbulb.*
  <https://www.skytopia.com/project/fractal/2mandelbulb.html>, 2007-2009. The Mandelbulb's
  triplex-power formula.
- **Paul Nylander.** *Hypercomplex fractals* gallery and notes.
  <https://www.bugman123.com/Hypercomplex/>. Companion to White's Mandelbulb explorations.
- **Tom Lowe.** *The Mandelbox* ("Amazing Fractal" thread), fractalforums.com, 2010. Box fold +
  sphere fold + scale — the Mandelbox family.
- **Knighty.** *Kaleidoscopic (escape time) IFS*, fractalforums.com, 2010.
  <http://www.fractalforums.com/3d-fractal-generation/kaleidoscopic-%28escape-time-ifs%29/>. The
  KIFS fold scheme (sort-3 Menger, Octahedron, Dodecahedron folds; #1197), and the rotated
  pseudo-Kleinian DE used by the Kleinian family.
- **Amoser.** *Complex sine 3D formula* (fractalforums.org, topic 5591; login-walled, content from a
  user paste). <https://fractalforums.org/index.php?topic=5591.0>. Origin of the non-escaping DE work
  (`Docs/Technical/NonEscaping-DE-DevPlan.md`, #279).
- **Melinda Green.** *Buddhabrot rendering technique.* <http://superliminal.com/fractals/bbrot/>, 1993.
  And *The 4D Mandel/Juli/Buddhabrot Hologram*, <http://superliminal.com/fractals/bgram/bgram.html>.
- **Clifford A. Pickover.** *Computers, Pattern, Chaos, and Beauty.* St. Martin's Press, 1990. Source
  of the Clifford / De Jong attractor formulae used by the Strange Attractor family, the Magnet 1 / 2
  rational maps (renormalization of the Ising partition function), and the biomorph escape variant.
- **Clifford A. Pickover.** *Biomorphs: computer displays of biological forms generated from
  mathematical feedback loops.* Computers and the Imagination, 1986. The per-axis (`|Re z|` OR `|Im z|`)
  escape test behind biomorphs.
- **Barry Martin.** *Hopalong* attractor, 1986, as popularised by A. K. Dewdney's "Computer
  Recreations" column, Scientific American, 1986.
- **Earl F. Glynn.** The fractional-power ("Glynn") Julia set, 1990s — as cited by the Glynn help text.
- **Aristid Lindenmayer.** *Mathematical models for cellular interactions in development.* Journal
  of Theoretical Biology 18, 1968. L-systems.
- **T. A. Witten, L. M. Sander.** *Diffusion-limited aggregation, a kinetic critical phenomenon.*
  Physical Review Letters 47, 1981. The DLA growth model.
- **Alain Fournier, Don Fussell, Loren Carpenter.** *Computer rendering of stochastic models.*
  Communications of the ACM 25(6), 1982. Midpoint displacement / diamond-square height fields.

### Mandelbrot-set combinatorics and the location finder

Backs the interesting-location finder epic (#1184,
`Docs/Technical/Interesting-Location-Finder-DesignPlan.md` §10).

- **Tan Lei.** *Similarity between the Mandelbrot set and Julia sets.* Communications in Mathematical
  Physics 134, 1990. Local self-similarity of M at Misiurewicz points — the spiral finder and the
  Misiurewicz loop video.
- **Michał Misiurewicz.** *Absolutely continuous measures for certain maps of an interval.*
  Publications Mathématiques de l'IHÉS 53, 1981. The pre-periodic parameters named Misiurewicz points.
- **Pierre Lavaurs.** *Une description combinatoire de l'involution définie par M sur les rationnels à
  dénominateur impair.* Comptes Rendus Acad. Sci. Paris 303, 1986. The pairing of periodic external
  angles landing at one root.
- **Eike Lau, Dierk Schleicher.** *Internal addresses in the Mandelbrot set and irreducibility of
  polynomials.* Stony Brook IMS preprint, 1994. Internal addresses ↔ angles.
- **Paul Blanchard, Robert L. Devaney, Linda Keen.** *The dynamics of complex polynomials and
  automorphisms of the shift.* Inventiones Mathematicae 104, 1991. Monodromy (dual-orbit research).
- **Robert P. Munafo.** *Mu-Ency — The Encyclopedia of the Mandelbrot Set.* <https://mrob.com/pub/muency.html>.
  Period detection (box method), nuclei, Misiurewicz points, binary decomposition, external angles.
- **Claude Heiland-Allen** — Newton's method for nuclei and Misiurewicz points, atom-domain size,
  external-ray tracing, Julia morphing (mathr.co.uk; maths only, clean-room). See the
  [deep-zoom](#deep-zoom-acceleration) entries.

### Frontier & theoretical fractal R&D

Research-frontier references backing [Technical → Theoretical & Frontier Fractal R&D](Technical/Theoretical-Fractal-RnD.md).
Several are theory pointers (little concrete reference imagery); citations here are load-bearing
because frontier claims cannot be sanity-checked against existing renders.

<a id="markus-hess-lyapunov"></a>

- **Mario Markus, Benno Hess.** *Lyapunov exponents of the logistic map with periodic forcing.*
  Computers & Graphics 13(4), 1989. Origin of the Lyapunov ("Zircon Zity") fractal — a periodic
  A/B string schedules the logistic growth rate and each pixel is coloured by the Lyapunov exponent
  λ = lim (1/N) Σ ln|r_n(1 − 2x_n)|. Implemented by the `LyapunovCalculator`.
- **A. K. Dewdney.** *Leaping into Lyapunov space.* Scientific American, September 1991. Popularised
  the A/B-string Lyapunov images and the parameter-plane convention.
- **Robert L. Devaney.** *Work on entire transcendental dynamics — `λ exp z`, `λ sin z`, exploding
  Julia sets*, 1984–1990s. Julia sets of entire maps (no escape radius; Cantor bouquets / "hairs";
  imaginary-part / fast-escaping-set bailout).
- **Dominic Rochon.** *A generalized Mandelbrot set for bicomplex numbers.* Fractals 8(4), 2000.
  Basis for hypercomplex / split-algebra iteration (bicomplex, split-complex, coquaternion) via a
  pluggable product table.
- **David Mumford, Caroline Series, David Wright.** *Indra's Pearls: The Vision of Felix Klein.*
  Cambridge University Press, 2002. Kleinian-group limit sets — Grandma's recipe, the Maskit slice,
  breadth-first word enumeration. Backs both the 2D Indra's Pearls renderer and the 3D Kleinian
  generalization.

<a id="kleinian-generalization"></a>

- **Bernard Maskit.** *Kleinian Groups.* Springer Grundlehren der mathematischen Wissenschaften 287,
  1988. Fundamental domains, the Maskit slice, discreteness criteria — the reference for the 3D
  Kleinian-generalization design (`Docs/Technical/Kleinian-Generalization-DesignPlan.md`).
- **Lars V. Ahlfors.** *Möbius transformations in several dimensions* (Univ. Minnesota, 1981) and
  *Möbius transformations and Clifford numbers* (in *Differential Geometry and Complex Analysis*,
  Springer 1985). Vahlen-matrix / Clifford-algebra representation of the conformal group `Möb(Ŝⁿ)` —
  basis for the Kleinian analytic distance estimator (full-Jacobian form).
- **John C. Hart, Daniel J. Sandin, Louis H. Kauffman.** *Ray tracing deterministic 3-D fractals.*
  ACM SIGGRAPH Computer Graphics 23(3), 1989. The distance-estimator sphere-tracing method underlying
  every FF 3D raymarcher (Mandelbulb, Mandelbox, Kleinian).
- **Frederick Soddy** (*The kiss precise*, Nature 137, 1936) and **R. L. Graham, J. C. Lagarias, C. L.
  Mallows, A. R. Wilks, C. H. Yan** (*Apollonian circle packings: number theory*, J. Number Theory 100,
  2003). Descartes circle theorem and Apollonian packing — basis for the Kleinian Apollonian-extrusion
  preset.
- **Karl Theodor Vahlen.** *Über Bewegungen und complexe Zahlen.* Mathematische Annalen 55, 1902.
  Clifford-matrix representation of Möbius transformations — historical origin of the Ahlfors form.
- **Robert Riley.** *An elliptical path among the parabolic representations of the figure-eight knot
  group.* Mathematika 22, 1975 / related 1979 work. The Riley slice — two-parabolic-generator Kleinian
  groups; a group family for the 2D Indra's-Pearls renderer
  (`Docs/Technical/Indras-Pearls-2D-DesignPlan.md` §3.5).
- **Curtis T. McMullen.** *Hausdorff dimension and conformal dynamics III: Computation of dimension.*
  American J. Mathematics 120, 1998. Dimension of Kleinian limit sets / the Maskit-slice boundary —
  context for the 2D Indra's-Pearls limit-curve renders.
- **Michael F. Barnsley.** *Superfractals.* Cambridge University Press, 2006. V-variable IFS
  (condensation / code-tree selection among a finite fractal set) and fractal tops (address-map
  colouring of an IFS attractor).
- **Xavier Buff, Arnaud Chéritat.** *Quadratic Julia sets with positive area.* Annals of Mathematics
  176(2), 2012. Positive-Lebesgue-measure Julia sets, constructed via controlled near-parabolic
  perturbation — adjacent to parabolic implosion.
- **Benoit Mandelbrot.** *Intermittent turbulence in self-similar cascades.* J. Fluid Mechanics 62,
  1974. Multiplicative cascades / canonical multifractal measures.
- **Oded Schramm.** *Scaling limits of loop-erased random walks and uniform spanning trees.* Israel
  Journal of Mathematics 118, 2000. Schramm–Loewner Evolution (SLE).
- **Bertrand Duplantier, Scott Sheffield.** *Liouville quantum gravity and KPZ.* Inventiones
  Mathematicae 185, 2011. LQG random surfaces.

<a id="parabolic-implosion"></a>

- **Adrien Douady.** *Does a Julia set depend continuously on the polynomial?* (parabolic implosion),
  Proc. Symp. Applied Math. 49, 1994. The discontinuity of Julia sets at parabolic parameters — the
  flagship far-future R&D target.
- **Pierre Lavaurs.** *Systèmes dynamiques holomorphes: explosion de points périodiques paraboliques.*
  Thèse, Université Paris-Sud, 1989. Lavaurs maps / the limit of the imploding family.
- **Mitsuhiro Shishikura.** *The Hausdorff dimension of the boundary of the Mandelbrot set and Julia
  sets.* Annals of Mathematics 147(2), 1998. Parabolic renormalization; dim ∂M = 2.
- **Jean Écalle.** *Les fonctions résurgentes.* Publ. Math. Orsay, 1981–1985. Résurgence theory
  underlying the Écalle–Voronin horn-map invariants used by parabolic renormalization.

<a id="escape-geometry-scattering"></a>

The dual-orbit escape-geometry map (`Docs/Technical/Theoretical-Fractal-RnD.md` §3.6, epic #863) reads two
points of the Julibrot (z₀, c) space that share their parameter — the critical orbit and a second seed — and
colours their *relation*. The sets themselves are known (Mandelbrot, perturbed-z₀ Mandelbrot, Julibrot
slices, the bicomplex quadratic); the relational colourings are what FF adds (novelty research #1130:
`Docs/Technical/DualOrbit-Coloring-RnD.md` §8). It sits in the chaotic-scattering / fractal-basin-boundary /
finite-size-Lyapunov literature, which these entries anchor.

- **Edward Ott, Tamás Tél.** *Chaotic scattering: an introduction.* Chaos 3(4), 1993. Scattering
  functions, escape time, exit basins, angular observables — the vocabulary §3.6's derived scalars
  (`dualOrbitAngle`, `scatteringAngle`, `Δn`) borrow.
- **Celso Grebogi, Edward Ott, James A. Yorke.** *Fractal basin boundaries, long-lived chaotic
  transients, and unstable-unstable pair bifurcation.* Physical Review Letters 50, 1983. Fractal basin
  boundaries — the §3.6 `Δn` / exit-channel structure.
- **Celso Grebogi, Steven W. McDonald, Edward Ott, James A. Yorke.** *Final state sensitivity: an
  obstruction to predictability.* Physics Letters A 99, 1983. The uncertainty exponent
  (`UncertaintyExponent`, #1127).
- **Alessandro Daza, Alexandre Wagemakers, Bertrand Georgeot, David Guéry-Odelin, Miguel A. F.
  Sanjuán.** *Basin entropy: a new tool to analyze uncertainty in dynamical systems.* Scientific
  Reports 6, 2016. `BasinEntropy` (#1127).
- **Helena E. Nusse, James A. Yorke.** *Wada basin boundaries and basin cells.* Physica D 90, 1996.
  Wada (three-or-more-way) exit basins.
- **David Sweet, Edward Ott, James A. Yorke.** *Topology in chaotic scattering.* Nature 399, 315–316,
  27 May 1999 (scientific correspondence). The four-mirror-ball Wada photograph: four touching mirrored
  balls stacked as a tetrahedron, a light ray leaving the inner chamber through one of four openings in
  the tetrahedron's faces, the four exit basins sharing one fractal boundary (dimension ≈ 1.6). Volume,
  pages, date and the experimental setup were checked against the article PDF attached to #1444. It is
  the target of the Wada mirror-spheres epic (#1439–#1444): `MirrorSpheresCalculator` (#1440) traces
  that scatterer. It is also context for the chaotic-billiard modes (#1445). Paul Bourke's page (below)
  lists the article as *Complex topology in chaotic scattering: a laboratory observation*, which may be
  a preprint title.

<a id="schlick-fresnel"></a>

- **Christophe Schlick.** *An inexpensive BRDF model for physically-based rendering.* Computer Graphics
  Forum 13(3), 1994. doi:10.1111/1467-8659.1330233, <https://diglib.eg.org/handle/10.2312/6905>.
  Schlick's Fresnel approximation F0 + (1 − F0)(1 − cos θ)⁵. FF uses it for the mirror-spheres photo
  mode's per-bounce reflectance (`MirrorSpheresScene.Schlick`, #1441), and the GPU shading kernels
  already used it (`GpuKernelUtils`, P7c.3). Author, year and DOI are from the Eurographics Digital
  Library record. The page range 233–246 is from Wikipedia (*Schlick's approximation*) and isn't yet
  checked against the journal.

<a id="yoneyama-lakes-of-wada"></a>

- **Kunizô Yoneyama.** *Theory of continuous set of points (not finished).* Tohoku Mathematical
  Journal, First Series 12, 43–158, 1917.
  <https://www.jstage.jst.go.jp/article/tmj1911/12/0/12_0_43/_article>. The "Lakes of Wada"
  construction, which Yoneyama credited to Takeo Wada: three or more disjoint open regions that share
  one common boundary. The Wada property the mirror-spheres basins (#1440) show is named after it. The
  title, volume and pages are from the J-STAGE listing. That the lakes appear on page 60 and are
  credited to Wada comes from secondary sources (Wikipedia, *Lakes of Wada*); it isn't yet checked
  against the paper's text.

<a id="bourke-wada"></a>

- **Paul Bourke.** *Wada basins: rendering chaotic scattering.* <https://paulbourke.net/fractals/wada/>,
  December 1998 (addition December 2000). Prior art for ray-traced renders of the four-ball Wada
  scatterer (Tachyon and POV-Ray models). It uses the same tangent arrangement FF uses (centres at
  even-parity cube corners, radius √2 at FF's scale), coloured lights at three gaps and the camera at
  the fourth, and notes that the number of reflections traced per ray is the setting that matters.
  `MirrorSpheresCalculator` (#1440) builds on this kind of render; it is not new as an image. Linked by
  the user on #1444.
- **E. Aurell, G. Boffetta, A. Crisanti, G. Paladin, A. Vulpiani.** *Predictability in the large: an
  extension of the concept of Lyapunov exponent.* Journal of Physics A 30, 1997. Finite-size Lyapunov
  exponent (FSLE) — the finite-separation reading of `D = E_c − E_z`.
- **George Haller.** *Lagrangian coherent structures.* Annual Review of Fluid Mechanics 47, 2015. FTLE
  ridges / transport barriers — the applied twin of the §3.6 separation field (fluid mixing, dispersal,
  predictability).
- **Brian Cabral, Leith Leedom.** *Imaging vector fields using line integral convolution.* SIGGRAPH
  1993. The LIC flow texture (#1128).
- **Alan Norton.** *Generation and display of geometric fractals in 3-D.* Computer Graphics (SIGGRAPH)
  16(3), 1982. Quaternion Julia rendering — basis for §3.6's quaternion variant and 4D→3D projection.

<a id="bourke-random-tile"></a>

- **Paul Bourke.** *Random space filling of the plane.*
  <https://paulbourke.net/fractals/randomtile/>, 2011. The power-law shape-size distribution and
  random non-overlapping placement scheme implemented by the `RandomTile` calculator — shapes of
  radius $r_i = r_{\max}/(i+1)^{1/\alpha}$ dropped at random positions and rejected on overlap until
  the plane fills. See [RandomTile dev plan](Technical/RandomTile-Plan.md).

<a id="deep-zoom-acceleration"></a>

### Deep zoom: perturbation, approximation, extended precision

<a id="kevin-i-martin-superfractalthing"></a>

- **Kevin I. Martin.** *SuperFractalThing* and *Superfractalthing Maths*, 2013, announced at
  <https://www.fractalforums.com/announcements-and-news/superfractalthing-arbitrary-precision-mandelbrot-set-rendering-in-java/>.
  The original published perturbation + series approximation algorithm now widely re-implemented
  (including in this project).
- **Pauldelbrot.** *Glitch detection in perturbation Mandelbrot* (fractalforums.com thread), 2014. The
  original glitch-detection criterion.
- **Zhuoran.** *Another solution to perturbation glitches*, fractalforums.org, 2021.
  <https://fractalforums.org/fractal-mathematics-and-new-theories/28/another-solution-to-perturbation-glitches/4360>.
  Rebasing and bilinear approximation (BLA) — `Engine/Math/Bla.cs`, CalcGen's BLA emitter,
  `AllowPtRebasing`.
- **Claude Heiland-Allen.** *MandelbrotPerturbator* implementation notes and the Mandelbrot book draft.
  <https://mathr.co.uk/mandelbrot/book-draft/>. Practical writeup of perturbation theory + bilinear
  approximation, glitch detection, and rebase reference selection; maintainer of Kalles Fraktaler 2+
  (<https://mathr.co.uk/kf/kf.html>).
- **Karl Runmo.** *Kalles Fraktaler.* <http://www.chillheimer.de/kallesfraktaler/>. The reference
  deep-zoom Mandelbrot renderer; pioneered many perturbation / SA / glitch techniques this engine
  uses, and Newton-Raphson zooming (location finder reference behaviour).
- **Botond Kósa.** *Mandel Machine.* Perturbation deep-zoom renderer — prior art for reference-orbit
  pairs (dual-orbit survey).
- **Yozo Hida, Xiaoye S. Li, David H. Bailey.** *Algorithms for quad-double precision floating point
  arithmetic.* Proc. 15th IEEE Symposium on Computer Arithmetic (ARITH-15), 2001 (LBNL report, 2000),
  and the *QD library* (<https://www.davidhbailey.com/dhbsoftware/>). Foundation for the DD / QD
  pipeline.
- **T. J. Dekker.** *A floating-point technique for extending the available precision.* Numerische
  Mathematik 18, 1971. The splitting constant and exact two-product behind DD / QD (`GpuQD.cs`).
- **Jonathan Richard Shewchuk.** *Adaptive precision floating-point arithmetic and fast robust
  geometric predicates.* Discrete & Computational Geometry 18(3), 1997. The exact expansion sum used
  by octuple-double addition (`OctupleDouble.cs`, #1319).
- **Mariani–Silver subdivision** (Rico Mariani, Robert Silver). Rectangle subdivision that fills a
  box whose border is uniformly in-set — solid-region skipping (#1343; since #1394 part 2 also the
  Julia and Multibrot families, `SolidRegionSubdivision.cs`; since #1394 part 1 on the GPU kernels too,
  as masked dispatches, `GpuPixelMask`). See
  [Citations awaiting verification](#citations-awaiting-verification) for the primary source.
- **Wikipedia contributors.** *Plotting algorithms for the Mandelbrot set*, section "Cardioid / bulb
  checking". <https://en.wikipedia.org/wiki/Plotting_algorithms_for_the_Mandelbrot_set> (accessed
  2026-10-25). The closed-form main-cardioid test in its square-root-free form, q(q + (x − 1/4)) ≤ y²/4
  with q = (x − 1/4)² + y², and the period-2 bulb test (x + 1)² + y² ≤ 1/16 —
  `MandelbrotCalculator.IsInMainCardioidOrBulb` (the in-set early-out), and since #1394 the "free
  in-set pixel" test solid-region Auto uses to decide whether a frame is worth subdividing. The same
  article's "Rectangle checking" section describes Mariani–Silver as a variant.
- **Richard P. Brent.** *An improved Monte Carlo factorization algorithm.* BIT 20, 1980. Brent's
  cycle detection — the periodicity early-out for bounded orbits.

### Distance estimation, normals, shading

- **John Hart.** *Sphere tracing: A geometric method for the antialiased ray tracing of implicit
  surfaces.* The Visual Computer, 1996. The original distance-estimation raymarcher.
- **Benjamin Keinert, Henry Schäfer, Johann Korndörfer, Urs Ganse, Marc Stamminger.** *Enhanced
  Sphere Tracing.* Smart Tools and Apps for Graphics (Eurographics Italian Chapter), 2014.
  Over-relaxed sphere tracing (`SphereRelax.cs`, `MarchRelaxation`, #1335).
- **Inigo Quilez.** *Distance estimation, ambient occlusion, soft shadows for raymarched fractals.*
  <https://iquilezles.org/articles/>. The canonical modern primer.
- **Robert L. Cook, Kenneth E. Torrance.** *A reflectance model for computer graphics.* SIGGRAPH 1982.
  Source for the PBR3D theme kind's microfacet shading.
- **Brian Karis.** *Real Shading in Unreal Engine 4.* SIGGRAPH 2013 Physically Based Shading course.
  Split-sum prefiltered IBL, the light range window falloff (cited as "Karis 2014" in `HdriRegistry`).
- **Eric Heitz.** *Sampling the GGX distribution of visible normals.* Journal of Computer Graphics
  Techniques 7(4), 2018. GGX VNDF reflection sampling.
- **Jeppe Revall Frisvad.** *Building an orthonormal basis from a 3D unit vector without
  normalization.* Journal of Graphics Tools 16(3), 2012. Branchless TBN in `ShadingPipeline`.
- **Sébastien Hillaire.** *Physically based and unified volumetric rendering in Frostbite.* SIGGRAPH
  2015 Advances in Real-Time Rendering course. The froxel volume (`FroxelVolume.cs`).
- **L. McIntosh, B. E. Riecke, S. DiPaola.** *Efficiently simulating the bokeh of polygonal apertures
  in a post-process depth of field shader.* Computer Graphics Forum 31(6), 2012. HDR hex-bokeh DoF.
- **Jorge Jimenez.** *Next generation post processing in Call of Duty: Advanced Warfare.* SIGGRAPH
  2014 Advances in Real-Time Rendering course. Interleaved-gradient noise.
- **Harold W. Thimbleby, Stuart Inglis, Ian H. Witten.** *Displaying 3D images: algorithms for
  single-image random-dot stereograms.* IEEE Computer 27(10), 1994. Constraint linking with
  hidden-surface removal in the `Autostereogram` engine (#1013).
- **J. H. Halton.** *On the efficiency of certain quasi-random sequences of points in evaluating
  multi-dimensional integrals.* Numerische Mathematik 2, 1960. The Halton(2,3) sub-pixel jitter used
  by TAA and supersampling.

### Denoising and image quality

- **Holger Dammertz, Daniel Sewtz, Johannes Hanika, Hendrik P. A. Lensch.** *Edge-avoiding À-Trous
  wavelet transform for fast global illumination filtering.* High Performance Graphics 2010.
  `AtrousDenoiser`.
- **Christoph Schied et al.** *Spatiotemporal variance-guided filtering: real-time reconstruction for
  path-traced global illumination.* High Performance Graphics 2017. The temporal half of SVGF
  (`SvgfTemporal.cs`).
- **Intel Open Image Denoise (OIDN).** <https://www.openimagedenoise.org>, Apache-2.0; runtime fetched
  from <https://github.com/RenderKit/oidn/releases> with a pinned SHA-256. The opt-in learned denoiser
  (#1338).
- **Zhou Wang, Alan C. Bovik, Hamid R. Sheikh, Eero P. Simoncelli.** *Image quality assessment: from
  error visibility to structural similarity.* IEEE Transactions on Image Processing 13(4), 2004. SSIM
  (OIDN probe, visual regression).

---

## Colour theory and palettes

<a id="linas-vepstas-smooth-iter"></a>

- **Linas Vepstas.** *Renormalizing the Mandelbrot escape*, 1997.
  <https://linas.org/art-gallery/escape/escape.html>. The smoothed (continuous) escape count.
- **Garry T. Krollman.** *CIELAB and CIECAM02 conversions* — referenced by the from-image palette
  k-means sampler. Practical formulae from
  <https://www.brucelindbloom.com/index.html?Eqn_RGB_to_XYZ.html>.
- **Björn Ottosson.** *A perceptual color space for image processing* (Oklab), 2020.
  <https://bottosson.github.io/posts/oklab/>. OkLab distance in palette extraction.
- **Cynthia A. Brewer.** *ColorBrewer 2.* <https://colorbrewer2.org>. Reference for accessible
  sequential / diverging palettes, used for several built-in themes.
- **Jamie R. Nuñez, Christopher R. Anderton, Ryan S. Renslow.** *Optimizing colormaps with
  consideration for color vision deficiency to enable accurate interpretation of scientific data.*
  PLOS ONE 13(7), 2018. The Cividis colour map.
- **Gustavo M. Machado, Manuel M. Oliveira, Leandro A. F. Fernandes.** *A physiologically-based model
  for simulation of color vision deficiency.* IEEE Transactions on Visualization and Computer Graphics
  15(6), 2009. CVD simulation and the confusability linter (`CvdAnalysis.cs`).
- **Xiaodi Hou, Liqing Zhang.** *Saliency detection: a spectral residual approach.* CVPR 2007.
  PaletteBuilder's saliency map.
- **D. Sculley.** *Web-scale k-means clustering.* WWW 2010. Mini-batch k-means palette extraction.
- **Cyotek.** *Writing Adobe Swatch Exchange (ase) files using C#.*
  <https://www.cyotek.com/blog/writing-adobe-swatch-exchange-ase-files-using-csharp>. The ASE format
  layout used by the PaletteBuilder exporter.
- **Peter Karpov.** *Improved orbit-trap colouring for Mandelbrot zoom videos.*
  <https://inversed.ru/Blog_2.htm>. Direct inspiration for the orbit-trap theme kind.
- **NASA Ames perceptual papers** on chromostereopsis (red/blue depth cue). Source for the
  Chromostereopsis theme kind.

---

## QR codes

Backs the fractal QR epic (#1305, `Docs/Technical/Fractal-QR-Codes.md`).

- **ISO/IEC 18004.** *QR Code bar code symbology specification.* The Model 2 encoder (`QrEncoder.cs`):
  versions, ECC levels, masks, placement.
- **Irving S. Reed, Gustave Solomon.** *Polynomial codes over certain finite fields.* Journal of the
  Society for Industrial and Applied Mathematics 8(2), 1960. The error-correction codewords.
- **Thonky.com.** *QR Code Tutorial.* <https://www.thonky.com/qr-code-tutorial/>. The worked
  "HELLO WORLD" 1-M example the encoder tests check against.
- **Russ Cox.** *QArt Codes*, 2012. <https://research.swtch.com/qart>. Shaping the free data bits
  toward an image (#1402).
- **Hung-Kuo Chu, Chia-Sheng Chang, Ruen-Rone Lee, Niloy J. Mitra.** *Halftone QR Codes.* ACM
  Transactions on Graphics 32(6) (SIGGRAPH Asia), 2013. The 3x3 halftone module style.

---

## Audio analysis (audio-reactive slideshow)

- **Sebastian Böck, Markus Schedl.** *Maximum filter vibrato suppression for onset detection.*
  Proceedings of DAFx-13, 2013. Spectral-flux beat detection underpins the audio-reactive engine.
- **Brian C. J. Moore.** *An Introduction to the Psychology of Hearing.* Brill, 6th ed., 2012.
  Source for the band-weighted detector EQ (bass / lo-mid / mid / hi-mid / high) bands.

---

## Software, libraries, and tooling

### Direct dependencies (referenced from `*.csproj`)

| Library                | Purpose                                                                          | Project home                                                       |
|------------------------|----------------------------------------------------------------------------------|--------------------------------------------------------------------|
| Avalonia 12            | Cross-platform UI framework that hosts the active shell                          | <https://avaloniaui.net>                                           |
| Avalonia.Controls.ColorPicker | Colour swatch / colour wheel in the theme editor                          | <https://github.com/AvaloniaUI/Avalonia>                           |
| AvaloniaEdit           | Text editor control behind the DSL code view (#1362)                             | <https://github.com/AvaloniaUI/AvaloniaEdit>                       |
| ReactiveUI.Avalonia    | MVVM with observable bindings; the project's primary VM idiom                    | <https://www.reactiveui.net>                                       |
| Vortice.Windows        | Native-friendly D3D11/12 + DXGI bindings                                         | <https://github.com/amerkoleci/Vortice.Windows>                    |
| Silk.NET               | Cross-platform OpenGL / Vulkan / Metal bindings (cross-platform render path)     | <https://github.com/dotnet/Silk.NET>                               |
| SkiaSharp              | Software / OpenGL Skia renderer                                                  | <https://github.com/mono/SkiaSharp>                                |
| ILGPU                  | C# → SPIR-V / PTX / OpenCL JIT for the GPU calculator path                       | <https://www.ilgpu.net>                                            |
| Roslyn (`Microsoft.CodeAnalysis.CSharp`) | C# script compilation for User Equation + theme C# export      | <https://github.com/dotnet/roslyn>                                 |
| MathNet.Numerics       | Numerical routines (linear algebra, special functions)                           | <https://numerics.mathdotnet.com>                                  |
| ZXing.Net              | QR decoding — every fractal QR export is verified to scan                        | <https://github.com/micjahn/ZXing.Net>                             |
| QuestPDF               | PDF export                                                                       | <https://www.questpdf.com>                                         |
| Markdig                | Markdown → HTML for the static documentation site                                | <https://github.com/xoofx/markdig>                                 |
| NAudio                 | WASAPI loopback + microphone capture for the audio-reactive engine               | <https://github.com/naudio/NAudio>                                 |
| FFmpeg                 | Lossless and visually-lossless video encoding presets                            | <https://ffmpeg.org>                                               |
| Intel Open Image Denoise | Opt-in learned denoiser, fetched at runtime (see Denoising above)              | <https://www.openimagedenoise.org>                                 |
| BenchmarkDotNet        | Micro-benchmarks (`Benchmarks/`)                                                 | <https://benchmarkdotnet.org>                                      |
| xUnit                  | Test framework (`Server.Tests/`)                                                 | <https://xunit.net>                                                |
| KaTeX (CDN, web only)  | LaTeX rendering inside the static doc site                                       | <https://katex.org>                                                |
| Prism.js (CDN, web only) | Syntax highlighting inside the static doc site                                 | <https://prismjs.com>                                              |

### Algorithm references implemented in code

- **k-means in CIELAB** — Stuart P. Lloyd. *Least squares quantization in PCM.* IEEE Transactions on
  Information Theory, 1982. Underlies `PaletteBuilder.Lib`'s palette extractor and the theme editor's
  *From Image…* button.
- **Gaussian elimination with partial pivoting** — Numerical Recipes (Press et al., 3rd ed.). Used in
  the Newton calculator's polynomial-coefficient mode (roadmap).
- **Smoothstep / quintic Hermite interpolation** — Ken Perlin. *Improving noise.* SIGGRAPH 2002.
  Used for the slideshow cross-fade and the video-zoom easing curve.
- **Persistent threads** — Timo Aila, Samuli Laine. *Understanding the Efficiency of Ray Traversal on
  GPUs.* High-Performance Graphics 2009, pp. 145–149.
  <https://research.nvidia.com/publication/understanding-efficiency-ray-traversal-gpus> (Eurographics
  Digital Library: <https://diglib.eg.org/handle/10.2312/EGGH.HPG09.145-150>). A fixed pool of GPU
  threads that fetch work from a global counter instead of one thread per item — the idea the GPU
  escape-time kernels' wavefront schedule follows (#1340, `MandelbrotKernelSource.WaveFetch`), and why
  #1394 part 1 runs a sparse solid-region pass in it (the threads pull only marked pixels). Credit added
  with #1394; the attribution of the technique to this paper is to be confirmed against its text (see
  [Citations awaiting verification](#citations-awaiting-verification)).

### Scene Engine — camera splines, motion blur, cinematic moves

<a id="catmull-rom"></a>

- **Catmull-Rom splines** — Edwin Catmull, Raphael Rom. *A class of local interpolating splines.*
  In *Computer Aided Geometric Design*, Academic Press, 1974. The C¹-continuous interpolating spline
  the camera track uses by default — a curve that passes through every keyframe with tangents derived
  from the neighbouring keys. Used in `CameraTrack.Evaluate` and `SceneGlobalTrack.Evaluate`.
- **Cubic Hermite interpolation / smoothstep** — see the Perlin entry above. The Bezier interpolation
  mode and the per-key `EaseInOut` reparametrisation both reduce to smoothstep
  ($u^2(3-2u)$, a cubic Hermite with zero endpoint tangents).
- **Accumulation motion blur & shutter angle** — Rob Cook, Loren Carpenter, Edwin Catmull.
  *The Reyes image rendering architecture.* SIGGRAPH 1987. Distributes samples across the open-shutter
  interval and averages them; the offline scene renderer's `--motion-blur` sub-frame averaging is the
  same box-filter-over-the-shutter idea. Shutter *fraction* here is the film "shutter angle" expressed
  as a fraction of the frame interval (0.5 ≈ a 180° shutter).
- **Dolly zoom (the "Vertigo" / Hitchcock effect)** — coined in Alfred Hitchcock's *Vertigo* (1958),
  achieved by dollying the camera while zooming the opposite way. The orbit camera exposes *distance*
  (dolly) directly; a true field-of-view zoom track is future work (see the roadmap).
- **Reinhard tone mapping** — Erik Reinhard, Michael Stark, Peter Shirley, James Ferwerda.
  *Photographic tone reproduction for digital images.* SIGGRAPH 2002. The `Reinhard` /
  `ReinhardExtended` per-shot tone-map operators.
- **ACES filmic tone mapping** — Academy Color Encoding System, AMPAS. The `ACES` per-shot tone-map
  operator's filmic curve, via two published fits: **Krzysztof Narkowicz**, *ACES Filmic Tone Mapping
  Curve*, 2015 (<https://knarkowicz.wordpress.com/2016/01/06/aces-filmic-tone-mapping-curve/>; the
  `AcesFilmic` view transform), and **Stephen Hill**'s RRT+ODT fit, 2017, in Matt Pettineo's
  BakingLab (<https://github.com/TheRealMJP/BakingLab>; the lighting `Aces` operator).

### Fractal software and prior art

Programs and galleries the project measured itself against, learned from, or pays homage to.

- **Fractint** — Stone Soup Group (Bert Tyler, Timothy Wegner, Mark Peterson, Pieter Branderhorst),
  1989–. <https://en.wikipedia.org/wiki/Fractint>. Julibrot type, initial-orbit perturbation, and much
  of the classic formula zoo.
- **Ultra Fractal** — Frederik Slijkerman. <http://www.ultrafractal.com/>. Formula language, orbit
  traps (DMJ OrbitTrapsColoring), start-value perturbation.
- **Gnofract 4D** — Edwin Young. <https://gnofract4d.sourceforge.net/manual/maths.html>. The 4D
  (c, z₀) object and arbitrary 4D-rotated slices.
- **Fragmentarium** — Mikael Hvidtfeldt Christensen (Syntopia).
  <https://github.com/Syntopia/Fragmentarium>. Reference KIFS / Kleinian shaders (Knighty's folds) and
  Fragmentarium-style colouring conventions.
- **Richard Rosenman.** *Buddhabrot* plug-in, 2015. <https://www.richardrosenman.com/shop/buddhabrot/>.
  4D projection planes, initial-value mutation.
- **Jos Leys.** 3D fractals gallery (Julibrots). <https://www.josleys.com/show_gallery.php?galid=329>.
- **POV-Ray** `julia_fractal` (quaternion / hypercomplex Julia) — 3D Julibrot-style renders.
- **Wolfram Language** `MandelbrotSetBoettcher` — the Böttcher coordinate.
- **Basins.jl** (JuliaDynamics) — basin entropy, uncertainty exponent.
  <https://docs.juliahub.com/General/Basins/>.
- **Noah Spurrier.** *Acid Warp*, 1992. The DOS psychedelic-pattern program the clean-room Acid Warp
  mode pays homage to.

---

## Sites, communities, and other long-form references

- **fractalforums.com** — long-running fractal-rendering community (now an archive). Many of the
  perturbation and glitch-detection refinements implemented here were first vetted there.
- **fractalforums.org** ("Fractal Forums Two") — the active successor community; source of the BLA
  thread and the Amoser formula above. Many novel equation discoveries.
- **mathr.co.uk** — Claude Heiland-Allen's writing on Mandelbrot rendering. Frequent first-stop
  reference.
- **iquilezles.org** — Inigo Quilez's articles on procedural graphics, raymarching, signed
  distance functions, noise.
- **paulbourke.net** — Paul Bourke's fractal and geometry archive (<http://paulbourke.net/fractals/>).
- **Wikipedia** (<https://en.wikipedia.org>) — family overviews linked from the design guides:
  Mandelbrot set, Multibrot, Tricorn, Burning Ship, Phoenix, Newton fractal, Buddhabrot, *Plotting
  algorithms for the Mandelbrot set*, *List of fractals by Hausdorff dimension*.
- **shadertoy.com** — countless cross-reference shaders for the 3-D / distance-estimation pipeline.
- **scratchapixel.com** — Wikipedia-style explainer site for ray tracing, sampling, transforms.
- **redblobgames.com** — Amit Patel's interactive write-ups (hex grids, A*, easing). Indirect
  influence on UI animation design.

---

## Citations awaiting verification

These sources are cited in the code or docs, but their publication details aren't confirmed yet.
They stay listed (the contract never drops a credit). Each needs its primary source traced and its
entry above completed or corrected.

- **Botsch 2013** — "series approximation for `z² + c`", cited in
  `Docs/Technical/FractalEquation-DesignGuide.md` §15 as the basis for CalcGen's SA emitter. No
  matching publication found; series approximation for deep zoom is usually credited to K. I. Martin
  (2013, above).
- **Inou 2000** — cited by the Tricorn help text for "boundary has fractal dimension 2". Hiroyuki Inou
  has published on the Tricorn; the 2000 paper isn't identified.
- **Mariani–Silver** — primary description (Rico Mariani's algorithm; Robert Silver's formulation)
  isn't identified; it's commonly documented through Fractint-era material and Wikibooks *Fractals*.
- **Earl F. Glynn** — the original publication of the fractional-power Julia set.
- **Persistent threads (Aila & Laine 2009)** — the paper's details are confirmed (authors, venue,
  pages, URL); that it is where the persistent-thread work distribution the wavefront schedule follows
  was introduced is from memory, not yet checked against the paper's text.
- **Garry T. Krollman** — the CIELAB / CIECAM02 conversion attribution above; the formulae themselves
  are Bruce Lindbloom's.
- **NASA Ames perceptual papers** — which chromostereopsis papers.
- **Kalles Fraktaler 2 mirror** — an earlier revision of this page linked
  <https://github.com/edyoung/kalles-fraktaler-2>; the canonical KF2+ page is
  <https://mathr.co.uk/kf/kf.html>.
- **"Yuhao Zhu"** — an earlier revision of this page credited *Higher-precision arithmetic via
  double-double and quad-double* to Yuhao Zhu. The QD work it describes is Hida, Li and Bailey (above);
  any separate Zhu write-up hasn't been identified.

---

## Corrected attributions

When an entry is corrected (a wrong author, title, year, venue, URL or anchor), the old form is
recorded here. The credit history then stays visible, and anyone who saw the old credit can see
where it went. The citation guard's `--base` check passes because the old form is still on this
page. The convention:

- **Change the entry in place, then add a line here:** the old form (bold for a name, `<URL>` for a
  link), the new form, the date and the PR or issue, and why.
- **Renamed anchors keep their old `<a id>`** next to the new one, so existing links keep working.
- **Only corrections that are confirmed go here.** If it isn't settled, it goes under
  [Citations awaiting verification](#citations-awaiting-verification).
- **Never use this section to remove a credit.** A source that was credited but turns out unused is
  still credited. It was part of the project's history.

Corrections so far:

- 2026-10-25 (#1450). **Kevin I. Martin** — was *"Superfractalthing: Mandelbrot Set Calculation in
  High Precision", Bulletin of the Mandel-machine project, 2014*; now *SuperFractalThing* /
  *Superfractalthing Maths*, 2013, with the fractalforums.com announcement. No "Mandel-machine
  bulletin" publication could be identified.
- 2026-10-25 (#1450). **Botond Kósa** — was credited with *KallesFraktaler2* (linked to
  <https://github.com/edyoung/kalles-fraktaler-2>); now credited with *Mandel Machine*. Kalles
  Fraktaler is credited to **Karl Runmo** and KF2+ to Claude Heiland-Allen. The old link is under
  awaiting verification.
- 2026-10-25 (#1450). **Yuhao Zhu** — was credited with *Higher-precision arithmetic via
  double-double and quad-double*; the QD work is now credited to **Yozo Hida, Xiaoye S. Li, David H.
  Bailey**. The Zhu attribution is under awaiting verification.

---

## How to cite

When introducing a piece of mathematics or an algorithm in a doc page, link inline like this:

```markdown
The smoothed escape count follows [(Linas Vepstas 1997)](../Resources-Bibliography.md#linas-vepstas-smooth-iter).
```

…and ensure the anchor exists here. If it does not yet, add it as a one-line stub under the right
section and let the next pass flesh it out. The cost of a stub is zero; the cost of an unverifiable
claim is the trust readers put in the doc.

Before pushing, run the citation guard; CI runs it on every pull request:

```bash
python Tools/CitationGuard/citation_guard.py
```
