
namespace DIYLevelFastInit
{

    // One-slot result passed into nested coroutines (iterators cannot have out/ref params).
    internal sealed class Box<T>
    {
        public T Value;
    }
}
