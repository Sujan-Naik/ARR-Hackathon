// Role: kind of thing a module observes. Lives in the Internal assembly so modules and the tracker can use it.
// It stays in namespace AR.Vision, so game code uses it exactly as before.
namespace AR.Vision
{
    public enum ObservationKind
    {
        Face,
        Hand,
        Object
    }
}
