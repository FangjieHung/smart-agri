import { Repository } from './repository';

export class LocalStorageRepository<T extends { id: string }> implements Repository<T> {
  constructor(
    private readonly key: string,
    private readonly seed: () => T[],
    private readonly onReset?: () => void,
    private readonly normalize?: (item: unknown) => T,
  ) {}

  getAll(): T[] {
    const raw = localStorage.getItem(this.key);
    if (raw === null) return this.reset(false);
    try {
      const parsed = JSON.parse(raw);
      if (!Array.isArray(parsed)) return this.reset(true);
      const normalize = this.normalize;
      return normalize ? parsed.map((item) => normalize(item)) : (parsed as T[]);
    } catch {
      return this.reset(true);
    }
  }

  getById(id: string): T | undefined {
    return this.getAll().find((item) => item.id === id);
  }

  create(item: T): T {
    this.replaceAll([...this.getAll(), item]);
    return item;
  }

  update(id: string, patch: Partial<T>): T {
    const items = this.getAll();
    const idx = items.findIndex((item) => item.id === id);
    if (idx < 0) throw new Error(`not found: ${id}`);
    const updated = { ...items[idx], ...patch };
    items[idx] = updated;
    this.replaceAll(items);
    return updated;
  }

  remove(id: string): void {
    this.replaceAll(this.getAll().filter((item) => item.id !== id));
  }

  replaceAll(items: T[]): void {
    localStorage.setItem(this.key, JSON.stringify(items));
  }

  private reset(notify: boolean): T[] {
    const items = this.seed();
    this.replaceAll(items);
    if (notify) this.onReset?.();
    return items;
  }
}
