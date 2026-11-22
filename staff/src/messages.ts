import { ApiError } from './api';

const errors: Record<string, string> = {
  UNAUTHENTICATED: 'Войдите в кабинет, чтобы продолжить.',
  FORBIDDEN: 'Для этого действия нужна другая роль.',
  SESSION_CHANGED: 'Учётная запись изменилась. Обновите страницу.',
  QR_UNREADABLE: 'QR не читается. Переснимите бланк.',
  QR_MISMATCH: 'На снимке QR другого бланка.',
  INVALID_QR: 'QR не найден в реестре выбранной игры.',
  BLANK_ALREADY_ACCEPTED: 'Бланк уже принят или отправляется.',
  INTAKE_CLOSED: 'Приём по этой игре закрыт.',
  RETAKE_APPROVAL_REQUIRED: 'Для пересъёмки нужно разрешение администратора.',
  RETAKE_CONFLICT: 'Запрос пересъёмки изменился. Обновите список.',
  REVISION_CONFLICT: 'Вердикт уже изменился. Откройте дело заново.',
  ASSIGNMENT_EXPIRED: 'Срок назначения истёк. Получите актуальную карточку.',
  STALE_ASSIGNMENT: 'Назначение изменилось. Получите актуальную карточку.',
  APPEAL_ALREADY_USED: 'Команда уже обжаловала эту задачу.',
  APPEAL_DEADLINE_PASSED: 'Срок подачи апелляции истёк.',
  APPEAL_QUOTA_EXHAUSTED: 'Квота апелляций исчерпана.',
  APPEAL_NOT_ALLOWED: 'Для этой игры апелляции недоступны.',
};

export function message(error: unknown) {
  const code = error instanceof ApiError ? error.code : typeof error === 'string' ? error : undefined;
  return code ? errors[code] ?? ('Сервер вернул ошибку ' + code) : 'Проверьте подключение и повторите действие.';
}
