using System.Reflection;
using System.Threading.Tasks;

namespace Sisus.Init.Internal
{
	internal static class TaskExtensions
	{
		/// <summary>
		/// Converts a <paramref name="task"/> with any result type into a <see cref="Task{object}"/>.
		/// <para>
		/// If <paramref name="task"/> can be cast to <see cref="Task{object}"/>, then the result
		/// is gotten from awaiting the <see cref="Task{object}"/> object.
		/// </para>
		/// <para>
		/// Otherwise, the <paramref name="task"/> is awaited as is, and then reflection is used to
		/// retrieve the result from the <see cref="Task{T}.Result"/> property.
		/// </para>
		/// <para>
		/// If the <see cref="Task"/> is non-generic, with no result, then
		/// a new completed Task with a <see langword="null"/> result is returned.
		/// </para>
		/// </summary>
		/// <param name="task"> <see cref="Task"/> whose result to get. </param>
		/// <returns> object of type <see cref="Task{object}"/> </returns>
		internal static async Task<object> GetResult(this Task task)
		{
			object result;
			do
			{
				if(task is Task<object> objectTask)
				{
					result = await objectTask;
				}
				else
				{
					await task;
					if(task.GetType().GetProperty(nameof(Task<object>.Result)) is not PropertyInfo resultProperty)
					{
						return Task.FromResult<object>(null);
					}
					
					result = resultProperty.GetValue(task);
				}

				task = result as Task;
			}
			while(task is not null);

			return result;
		}
	}
}