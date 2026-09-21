import { Repository } from './repository';

export function createInMemoryRepo<T extends { id: string }>(initial: T[] = []): Repository<T> {
  let items = [...initial];
  return {
    getAll: () => [...items],
    getById: (id) => items.find((i) => i.id === id),
    create: (item) => {
      items.push(item);
      return item;
    },
    update: (id, patch) => {
      const idx = items.findIndex((i) => i.id === id);
      if (idx < 0) throw new Error(`not found: ${id}`);
      items[idx] = { ...items[idx], ...patch };
      return items[idx];
    },
    remove: (id) => {
      items = items.filter((i) => i.id !== id);
    },
    replaceAll: (next) => {
      items = [...next];
    },
  };
}
